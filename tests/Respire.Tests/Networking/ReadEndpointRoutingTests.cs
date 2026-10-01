using System.Text;
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
            .IsEquivalentTo(["ROLE", "GET", "ROLE", "EVALSHA_RO"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    private static byte[] Bulk(string value)
        => Encoding.ASCII.GetBytes($"${Encoding.ASCII.GetByteCount(value)}\r\n{value}\r\n");
}
