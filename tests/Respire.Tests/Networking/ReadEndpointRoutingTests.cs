using System.Text;
using Respire.Commands;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadEndpointRoutingTests
{
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();
    private static readonly byte[] ReplicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();

    [Test]
    public async Task ReadViewUsesReplicaWithoutTelemetryAndKeepsWritesOnPrimary()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole, Bulk("replica"));
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });

        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(await view.SetAsync("key", "new-value")).IsTrue();

        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "GET key"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["SET key new-value"]);
    }

    [Test]
    public async Task ReplicaPolicyRejectsWrongRoleAndReplicaPreferredFallsBackBeforeSending()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var wrongRole = new FakeRespServer(2, PrimaryRole, PrimaryRole);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", wrongRole.Port)],
        });

        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key"))
            .Throws<RespireConnectionException>();
        await Assert.That(await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).GetStringAsync("key"))
            .IsEqualTo("primary");
        await Assert.That(wrongRole.ReceivedCommands).IsEquivalentTo(["ROLE", "ROLE"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["GET key"]);
    }

    [Test]
    public async Task OnlyCatalogVerifiedReadOnlyCommandsAreEligible()
    {
        await Assert.That(ReadOnlyCommandCatalog.Contains("GET")).IsTrue();
        await Assert.That(ReadOnlyCommandCatalog.Contains("MGET")).IsTrue();
        await Assert.That(ReadOnlyCommandCatalog.Contains("SET")).IsFalse();
        await Assert.That(ReadOnlyCommandCatalog.Contains("CUSTOM.READ")).IsFalse();
    }

    [Test]
    public async Task CancellationDuringReplicaValidationStopsBeforeApplicationCommand()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole)
        {
            SuppressReply = command => command == "ROLE",
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica)
            .GetStringAsync("key", cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ReplicaRoleIsRevalidatedBeforeEachBorrow()
    {
        var promoted = 0;
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = new FakeRespServer(2, ReplicaRole)
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? Volatile.Read(ref promoted) == 0 ? ReplicaRole : PrimaryRole
                : Bulk("replica"),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        // Revalidate on every borrow so the promotion is observed by the next read.
        client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.Zero;
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await view.GetStringAsync("first")).IsEqualTo("replica");
        Volatile.Write(ref promoted, 1);
        await Assert.That(async () => await view.GetStringAsync("second")).Throws<RespireConnectionException>();
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "GET first", "ROLE"]);
    }

    [Test]
    public async Task StreamedAndReadOnlyScriptCommandsUseReplicaPolicy()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command == "EVALSHA_RO" ? ":1\r\n"u8.ToArray()
                : command == "GET key" ? Bulk("replica") : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await using (var stream = await view.Strings.GetStreamAsync("key"))
        {
            using var reader = new StreamReader(stream!);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("replica");
        }
        using var result = await view.Scripts.ExecuteAsync(RespireScript.Create("return 1", readOnly: true));
        await Assert.That(replica.ReceivedCommands.Select(command => command.Split(' ')[0]))
            .IsEquivalentTo(["ROLE", "GET", "EVALSHA_RO"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task CatalogReadOnlyFunctionUsesReplicaPolicy()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole, ":1\r\n"u8.ToArray());
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });

        using var result = await client.WithReadFrom(RespireReadFrom.Replica)
            .ExecuteAsync(RespireCommands.Scripting.FCALL_RO, ["read-function", 0]);

        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "FCALL_RO read-function 0"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ValidatedReplicaConnectionServesReadsWithoutRepeatingRole()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk("replica") : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.FromMinutes(1);
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => view.GetStringAsync($"key{index}").AsTask()));

        await Assert.That(replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal)))
            .IsEqualTo(8);
    }

    [Test]
    public async Task ReplicaMultiplexerRoundRobinUsesEachSelectedConnection()
    {
        var getConnectionIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(2, ReplicaRole)
        {
            ReplyOverride = (connectionId, command) =>
            {
                if (command.StartsWith("GET ", StringComparison.Ordinal))
                {
                    getConnectionIds.Add(connectionId);
                    return Bulk("replica");
                }

                return null;
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 2,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        for (var index = 0; index < 4; index++)
            await view.GetStringAsync($"key-{index}");

        await Assert.That(getConnectionIds.Distinct().Count()).IsEqualTo(2);
        await Assert.That(replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(2);
    }

    [Test]
    public async Task CursorReadsStayOnOneReplica()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        var scanReply = "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray();
        await using var first = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("SCAN ", StringComparison.Ordinal) ? scanReply : null,
        };
        await using var second = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("SCAN ", StringComparison.Ordinal) ? scanReply : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        for (var page = 0; page < 4; page++)
            using (await view.ExecuteAsync("SCAN", ["0"])) { }

        var firstScans = first.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal));
        var secondScans = second.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal));
        await Assert.That(firstScans + secondScans).IsEqualTo(4);
        await Assert.That(firstScans == 0 || secondScans == 0).IsTrue();
    }

    [Test]
    public async Task CursorReadsKeepReplicaSelectedAfterFirstPageFallback()
    {
        var recovered = 0;
        var firstRoleChecks = 0;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var firstCandidate = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? Interlocked.Increment(ref firstRoleChecks) == 1 && Volatile.Read(ref recovered) == 0
                    ? PrimaryRole
                    : ReplicaRole
                : command.StartsWith("SCAN ", StringComparison.Ordinal) ? ScanReply("99") : null,
        };
        await using var secondCandidate = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("SCAN ", StringComparison.Ordinal)
                ? ScanReply("1")
                : null,
        };
        var first = firstCandidate.Port < secondCandidate.Port ? firstCandidate : secondCandidate;
        var second = ReferenceEquals(first, firstCandidate) ? secondCandidate : firstCandidate;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        using (await view.ExecuteAsync("SCAN", ["0"])) { }
        await Assert.That(Volatile.Read(ref firstRoleChecks)).IsEqualTo(1);
        await Assert.That(first.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal)))
            .IsEqualTo(0);
        await Assert.That(second.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal)))
            .IsEqualTo(1);
        Volatile.Write(ref recovered, 1);
        using (await view.ExecuteAsync("SCAN", ["1"])) { }

        await Assert.That(first.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal)))
            .IsEqualTo(0);
        await Assert.That(second.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    private static byte[] ScanReply(string cursor)
        => Encoding.ASCII.GetBytes($"*2\r\n${cursor.Length}\r\n{cursor}\r\n*0\r\n");

    private static byte[] Bulk(string value)
        => Encoding.ASCII.GetBytes($"${Encoding.ASCII.GetByteCount(value)}\r\n{value}\r\n");
}
