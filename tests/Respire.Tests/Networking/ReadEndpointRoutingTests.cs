using System.Text;
using System.Net;
using System.Net.Sockets;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadEndpointRoutingTests
{
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();
    private static readonly byte[] ReplicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();

    [Test]
    public async Task ReplicaEndpointOrderingDeduplicatesDnsHostsWithoutCaseSensitivity()
    {
        var endpoints = ReadEndpointRouter.Order(
            [new("Replica.Example", 7001), new("replica.example", 7001), new("replica.example", 7002)]);

        await Assert.That(endpoints.Length).IsEqualTo(2);
        await Assert.That(endpoints[0].Port).IsEqualTo(7001);
        await Assert.That(endpoints[1].Port).IsEqualTo(7002);
    }

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

        client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.Zero;
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(await view.SetAsync("key", "new-value")).IsTrue();

        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "GET key"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["SET key new-value"]);
    }

    [Test]
    public async Task InlineRawReadsAndPreencodedDatabaseSizeUseReplicaView()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => ReplicaRole,
                "GET key" => Bulk("replica"),
                "DBSIZE" => ":42\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });

        client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.Zero;
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        using var raw = await view.ExecuteAsync("GET key");
        await view.ExecuteFireAndForgetAsync("GET key");
        var databaseSize = await view.Server.DatabaseSizeAsync();

        await Assert.That(raw.AsString()).IsEqualTo("replica");
        await Assert.That(databaseSize).IsEqualTo(42);
        await Assert.That(replica.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
        await Assert.That(replica.ReceivedCommands).Contains("DBSIZE");
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task TypedDescriptorRoutesReadEvenWhenOperationLabelIsNotCanonical()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole, Bulk("encoding"));
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });

        client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.Zero;
        var view = (RespireClient)client.WithReadFrom(RespireReadFrom.Replica);
        var result = await view.StringAsync(
            "OBJECT", new Cmd1(RespireCommands.Key.OBJECT_ENCODING.Verb, "key"), CancellationToken.None);

        await Assert.That(result).IsEqualTo("encoding");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "OBJECT ENCODING key"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task DescriptorSubcommandAndTypedModuleReadsUseReplicaPolicy()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command switch
            {
                "GET key" or "OBJECT ENCODING key" => Bulk("value"),
                "MEMORY USAGE key" => ":1\r\n"u8.ToArray(),
                "GEOSEARCH geo FROMLONLAT 0 0 BYRADIUS 1 m" => "*0\r\n"u8.ToArray(),
                "XPENDING stream group" or "XPENDING stream group - + 10" => "*0\r\n"u8.ToArray(),
                "XINFO STREAM stream" or "XINFO GROUPS stream" or "XINFO CONSUMERS stream group"
                    => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });

        client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.Zero;
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        using (await view.ExecuteAsync(RespireCommand.Create("GET"), "key")) { }
        var key = "key";
        using (await view.ExecuteAsync($"OBJECT ENCODING {key}")) { }
        using (await view.ExecuteAsync(RespireCommands.Server.MEMORY, "USAGE", "key")) { }
        await view.Geo.SearchAsync("geo", GeoSearchOrigin.FromCoordinates(0, 0), GeoSearchShape.Circle(1));
        await view.Streams.PendingSummaryAsync("stream", "group");
        await view.Streams.PendingAsync("stream", "group");
        await view.Streams.InfoAsync("stream");
        await view.Streams.GroupInfoAsync("stream");
        await view.Streams.ConsumerInfoAsync("stream", "group");

        await Assert.That(replica.ReceivedCommands.Where(command => command != "ROLE")).IsEquivalentTo(
        [
            "GET key",
            "OBJECT ENCODING key",
            "MEMORY USAGE key",
            "GEOSEARCH geo FROMLONLAT 0 0 BYRADIUS 1 m",
            "XPENDING stream group",
            "XPENDING stream group - + 10",
            "XINFO STREAM stream",
            "XINFO GROUPS stream",
            "XINFO CONSUMERS stream group",
        ]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ReplicaPolicyRejectsWrongRoleAndReplicaPreferredFallsBackDuringCooldown()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var wrongRole = new FakeRespServer(2, PrimaryRole, PrimaryRole);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            ReplicaEndpoints = [new("127.0.0.1", wrongRole.Port)],
        });

        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key"))
            .Throws<RespireConnectionException>();
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).GetStringAsync("key"))
            .IsEqualTo("primary");
        // The failed replica is skipped for one ReplicaRefreshInterval, so the fallback read
        // reaches the primary without another connection or ROLE attempt.
        await Assert.That(wrongRole.ReceivedCommands).IsEquivalentTo(["ROLE"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["GET key"]);
    }

    [Test]
    public async Task OnlyCatalogVerifiedReadOnlyCommandsAreEligible()
    {
        await Assert.That(RespireCommands.String.GET.ReadKind).IsEqualTo(ReadCommandKind.Read);
        await Assert.That(RespireCommands.String.MGET.ReadKind).IsEqualTo(ReadCommandKind.Read);
        await Assert.That(RespireCommands.String.SET.ReadKind).IsEqualTo(ReadCommandKind.None);
        await Assert.That(RespireCommand.Create("CUSTOM.READ").ReadKind).IsEqualTo(ReadCommandKind.None);
        await Assert.That(new Cmd1(RespireCommands.Key.OBJECT_ENCODING.Verb, "key").ReadKind)
            .IsEqualTo(ReadCommandKind.Read);
        await Assert.That(new Cmd1(Verbs.Get, "key").ReadKind).IsEqualTo(ReadCommandKind.Read);
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
        using var cancellation = new CancellationTokenSource();
        var pending = client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key", cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!replica.ReceivedCommands.Contains("ROLE")) await Task.Delay(5, timeout.Token);
        await Assert.That(client.IsConnected).IsFalse();
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
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
        client.Core.ReadRouter.RefreshInterval = TimeSpan.Zero;
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await view.GetStringAsync("first")).IsEqualTo("replica");
        Volatile.Write(ref promoted, 1);
        await Assert.That(async () => await view.GetStringAsync("second")).Throws<RespireConnectionException>();
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "GET first", "ROLE"]);
    }

    [Test]
    public async Task FailedRoleRevalidationRemovesCoolingReplicaFromConnectedState()
    {
        var failRevalidation = 0;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole, Bulk("replica"))
        {
            SuppressReply = command => command == "ROLE" && Volatile.Read(ref failRevalidation) != 0,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(300),
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.FromSeconds(1);
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        await Assert.That(await view.GetStringAsync("first")).IsEqualTo("replica");
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        Volatile.Write(ref failRevalidation, 1);
        await Assert.That(async () => await view.GetStringAsync("second")).Throws<RespireConnectionException>();

        // The last successful ROLE is stale but still recent. Cooldown makes this entry unusable.
        await Assert.That(client.IsConnected).IsFalse();
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
    [NotInParallel]
    public async Task ReadOnlyScriptTelemetryUsesSelectedReplicaEndpoint()
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
        var activities = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireTelemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("EVALSHA_RO", StringComparison.Ordinal))
                    activities.Enqueue(activity);
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using var result = await client.WithReadFrom(RespireReadFrom.Replica).Scripts
            .ExecuteAsync(RespireScript.Create("return 1", readOnly: true));

        var activity = activities.Single();
        await Assert.That(activity.GetTagItem("server.address")).IsEqualTo("127.0.0.1");
        await Assert.That(activity.GetTagItem("server.port")).IsEqualTo(replica.Port);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task ScriptAcquisitionFailureTelemetryUsesStandaloneEndpoint()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", port)],
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        var activities = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireTelemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("EVALSHA ", StringComparison.Ordinal))
                    activities.Enqueue(activity);
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Scripts.ExecuteAsync(
            RespireScript.Create("return 1"), cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();

        var activity = activities.Single();
        await Assert.That(activity.GetTagItem("server.address")).IsEqualTo("127.0.0.1");
        await Assert.That(activity.GetTagItem("server.port")).IsEqualTo(port);
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
        client.Core.ReadRouter.RefreshInterval = TimeSpan.FromMinutes(1);
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
            // Long enough that a slow machine never revalidates within the test.
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
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
        var roleChecks = 0;
        FakeRespServer? failed = null;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var first = new FakeRespServer(ReplicaRole);
        await using var second = new FakeRespServer(ReplicaRole);
        foreach (var server in new[] { first, second })
        {
            var self = server;
            // Whichever replica the first page tries first reports the wrong role once.
            server.ReplyOverride = (_, command) =>
            {
                if (command == "ROLE")
                {
                    if (Interlocked.Increment(ref roleChecks) == 1 && Volatile.Read(ref recovered) == 0)
                    {
                        Volatile.Write(ref failed, self);
                        return PrimaryRole;
                    }
                    return ReplicaRole;
                }
                return command.StartsWith("SCAN ", StringComparison.Ordinal) ? ScanReply("1") : null;
            };
        }
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ReplicaRefreshInterval = TimeSpan.Zero,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        using (await view.ExecuteAsync("SCAN", ["0"])) { }
        var failedReplica = Volatile.Read(ref failed)!;
        var serving = ReferenceEquals(failedReplica, first) ? second : first;
        Volatile.Write(ref recovered, 1);
        using (await view.ExecuteAsync("SCAN", ["1"])) { }

        await Assert.That(failedReplica.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal)))
            .IsEqualTo(0);
        await Assert.That(serving.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    [Test]
    public async Task TypedScansPinEachEnumerationAndSpreadAcrossReplicas()
    {
        static byte[]? Pages(string command) => command.StartsWith("SCAN 0 ", StringComparison.Ordinal)
            ? KeysPage("7", "a")
            : command.StartsWith("SCAN 7 ", StringComparison.Ordinal) ? KeysPage("0", "b") : null;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var first = new FakeRespServer(ReplicaRole) { ReplyOverride = (_, command) => Pages(command) };
        await using var second = new FakeRespServer(ReplicaRole) { ReplyOverride = (_, command) => Pages(command) };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        var firstScan = new List<string>();
        await foreach (var key in view.Keys.ScanAsync()) firstScan.Add(key);
        var secondScan = new List<string>();
        await foreach (var key in view.Keys.ScanAsync()) secondScan.Add(key);

        await Assert.That(firstScan).IsEquivalentTo(["a", "b"]);
        await Assert.That(secondScan).IsEquivalentTo(["a", "b"]);
        // Each enumeration keeps both of its pages on one replica, and the two enumerations
        // were spread across both replicas.
        await Assert.That(ScanCursors(first)).IsEquivalentTo(["0", "7"]);
        await Assert.That(ScanCursors(second)).IsEquivalentTo(["0", "7"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task TypedScanFailsWhenItsReplicaFailsInsteadOfContinuingElsewhere()
    {
        var promoted = 0;
        FakeRespServer? pinned = null;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var first = new FakeRespServer(ReplicaRole);
        await using var second = new FakeRespServer(ReplicaRole);
        foreach (var server in new[] { first, second })
        {
            var self = server;
            server.ReplyOverride = (_, command) =>
            {
                if (command == "ROLE")
                    return Volatile.Read(ref promoted) == 1 && ReferenceEquals(self, Volatile.Read(ref pinned))
                        ? PrimaryRole : ReplicaRole;
                if (command.StartsWith("SCAN 0 ", StringComparison.Ordinal))
                {
                    Interlocked.CompareExchange(ref pinned, self, null);
                    return KeysPage("7", "a");
                }
                return command.StartsWith("SCAN ", StringComparison.Ordinal) ? KeysPage("0", "b") : null;
            };
        }
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ReplicaRefreshInterval = TimeSpan.Zero,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.ReplicaPreferred);

        var enumerator = view.Keys.ScanAsync().GetAsyncEnumerator();
        try
        {
            await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
            await Assert.That(enumerator.Current).IsEqualTo("a");
            Volatile.Write(ref promoted, 1);

            // The cursor belongs to the promoted node, so the enumeration fails rather than
            // continuing it on another replica or falling back to the primary.
            await Assert.That(async () => await enumerator.MoveNextAsync()).Throws<RespireConnectionException>();
        }
        finally { await enumerator.DisposeAsync(); }

        var other = ReferenceEquals(Volatile.Read(ref pinned), first) ? second : first;
        await Assert.That(other.ReceivedCommands.Any(command => command.StartsWith("SCAN ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("SCAN ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task SharedCursorPinIsDroppedAfterItsReplicaFails()
    {
        var promoted = 0;
        FakeRespServer? pinned = null;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var first = new FakeRespServer(ReplicaRole);
        await using var second = new FakeRespServer(ReplicaRole);
        foreach (var server in new[] { first, second })
        {
            var self = server;
            server.ReplyOverride = (_, command) =>
            {
                if (command == "ROLE")
                    return Volatile.Read(ref promoted) == 1 && ReferenceEquals(self, Volatile.Read(ref pinned))
                        ? PrimaryRole : ReplicaRole;
                if (!command.StartsWith("SCAN ", StringComparison.Ordinal)) return null;
                Interlocked.CompareExchange(ref pinned, self, null);
                return ScanReply("0");
            };
        }
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ReplicaRefreshInterval = TimeSpan.Zero,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        using (await view.ExecuteAsync("SCAN", ["0"])) { }
        var stale = Volatile.Read(ref pinned)!;
        var other = ReferenceEquals(stale, first) ? second : first;
        Volatile.Write(ref promoted, 1);

        // The pinned replica fails once; the next cursor command selects a healthy replica.
        await Assert.That(async () => await view.ExecuteAsync("SCAN", ["0"])).Throws<RespireConnectionException>();
        using (await view.ExecuteAsync("SCAN", ["0"])) { }

        await Assert.That(stale.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(other.ReceivedCommands.Count(command => command.StartsWith("SCAN ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task RawCursorContinuationFailsInsteadOfMovingToAnotherServer()
    {
        var promoted = 0;
        FakeRespServer? pinned = null;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var first = new FakeRespServer(ReplicaRole);
        await using var second = new FakeRespServer(ReplicaRole);
        foreach (var server in new[] { first, second })
        {
            var self = server;
            server.ReplyOverride = (_, command) =>
            {
                if (command == "ROLE")
                    return Volatile.Read(ref promoted) == 1 && ReferenceEquals(self, Volatile.Read(ref pinned))
                        ? PrimaryRole : ReplicaRole;
                if (!command.StartsWith("SCAN ", StringComparison.Ordinal)) return null;
                Interlocked.CompareExchange(ref pinned, self, null);
                return ScanReply("7");
            };
        }
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ReplicaRefreshInterval = TimeSpan.Zero,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        using (await view.ExecuteAsync("SCAN", ["0"])) { }
        var stale = Volatile.Read(ref pinned)!;
        var other = ReferenceEquals(stale, first) ? second : first;
        Volatile.Write(ref promoted, 1);

        // The issuing replica fails, which drops the shared pin.
        await Assert.That(async () => await view.ExecuteAsync("SCAN", ["7"])).Throws<RespireConnectionException>();
        // Its cursor cannot continue anywhere else, so later pages fail without reaching a server.
        await Assert.That(async () => await view.ExecuteAsync("SCAN", [7])).Throws<RespireConnectionException>();
        await Assert.That(async () => await view.ExecuteAsync($"SCAN {"7"} COUNT {10}")).Throws<RespireConnectionException>();
        await Assert.That(ScanCursors(other)).IsEmpty();
        // A fresh scan selects a healthy replica.
        using (await view.ExecuteAsync("SCAN", ["0"])) { }

        await Assert.That(ScanCursors(other)).IsEquivalentTo(["0"]);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("SCAN ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ConcurrentFirstRawCursorReadsShareOnePin()
    {
        static byte[]? Reply(string command) => command.StartsWith("SCAN ", StringComparison.Ordinal) ? ScanReply("0") : null;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var first = new FakeRespServer(ReplicaRole) { ReplyOverride = (_, command) => Reply(command) };
        await using var second = new FakeRespServer(ReplicaRole) { ReplyOverride = (_, command) => Reply(command) };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            using var result = await view.ExecuteAsync("SCAN", ["0"]);
        }));

        var firstScans = ScanCursors(first).Length;
        var secondScans = ScanCursors(second).Length;
        await Assert.That(firstScans + secondScans).IsEqualTo(16);
        await Assert.That(firstScans == 0 || secondScans == 0).IsTrue();
    }

    [Test]
    public async Task CursorPinToReplacedPrimaryIsNotReused()
    {
        static byte[]? Reply(string command) => command.StartsWith("SCAN ", StringComparison.Ordinal) ? ScanReply("0") : null;
        await using var primary = new FakeRespServer(FakeRespServer.OkReply) { ReplyOverride = (_, command) => Reply(command) };
        await using var replica = new FakeRespServer(ReplicaRole);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.PrimaryPreferred);
        // Models a failover: the pin records a primary multiplexer that the client has since replaced.
        await using var replaced = RespireConnectionMultiplexer.Create("127.0.0.1", 1);
        var cursors = client.Core.ReadRouter.Cursors;

        cursors.PinShared(RespireReadFrom.PrimaryPreferred, new ReadAffinity { Primary = replaced });
        await Assert.That(async () => await view.ExecuteAsync("SCAN", ["5"])).Throws<RespireConnectionException>();

        cursors.PinShared(RespireReadFrom.PrimaryPreferred, new ReadAffinity { Primary = replaced });
        using (await view.ExecuteAsync("SCAN", ["0"])) { }
        await Assert.That(cursors.TryGetShared(RespireReadFrom.PrimaryPreferred, out var repinned)).IsTrue();
        await Assert.That(ReferenceEquals(repinned!.Primary, client.Core.Multiplexer)).IsTrue();

        // A typed enumeration pinned to the replaced primary fails rather than continuing elsewhere.
        await Assert.That(async () => await client.Core.ReadRouter.GetCursorConnectionAsync(
                RespireReadFrom.PrimaryPreferred, new ReadAffinity { Primary = replaced }, isContinuation: true,
                CancellationToken.None))
            .Throws<RespireConnectionException>();
        await Assert.That(ScanCursors(primary)).IsEquivalentTo(["0"]);
        await Assert.That(replica.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task CursorContinuationIsReadFromTheCursorArgument()
    {
        var hscan = RespireCommands.All.ToArray().First(command => command.Name == "HSCAN");
        await Assert.That(CursorCommandMetadata.IsCursorContinuation("SCAN", new CmdN(Verbs.Scan, ["0", "COUNT", 10]))).IsFalse();
        await Assert.That(CursorCommandMetadata.IsCursorContinuation("SCAN", new CmdN(Verbs.Scan, [0]))).IsFalse();
        await Assert.That(CursorCommandMetadata.IsCursorContinuation("SCAN", new CmdN(Verbs.Scan, ["17"]))).IsTrue();
        await Assert.That(CursorCommandMetadata.IsCursorContinuation("hscan", new CatalogCommand(hscan, ["key", "0"]))).IsFalse();
        await Assert.That(CursorCommandMetadata.IsCursorContinuation("HSCAN", new CatalogCommand(hscan, ["key", "9"]))).IsTrue();
        await Assert.That(CursorCommandMetadata.IsCursorContinuation("ZSCAN",
            new DynamicCommand(["ZSCAN", "key", "9"], routingKeyIndex: 1))).IsTrue();
        await Assert.That(CursorCommandMetadata.IsCursorContinuation("SSCAN",
            new DynamicCommand(["SSCAN", "key", "0"], routingKeyIndex: 1))).IsFalse();
        // ARSCAN's cursor position is unknown, so it is always treated as a fresh scan.
        await Assert.That(CursorCommandMetadata.IsCursorContinuation("ARSCAN",
            new DynamicCommand(["ARSCAN", "key", "9"], routingKeyIndex: 1))).IsFalse();
    }

    [Test]
    public async Task FireAndForgetReadUsesReplicaPolicy()
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
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        await view.ExecuteFireAndForgetAsync(RespireCommands.String.GET, "fire");
        // The same replica connection answers in order, so this read follows the fire-and-forget one.
        await Assert.That(await view.GetStringAsync("after")).IsEqualTo("replica");

        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "GET fire", "GET after"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(1000, 0, 1000)]
    [Arguments(1000, 1, 1000)]
    [Arguments(1000, 2, 2000)]
    [Arguments(1000, 4, 8000)]
    [Arguments(1000, 6, 30000)]
    [Arguments(1000, 1000, 30000)]
    [Arguments(60000, 5, 60000)]
    [Arguments(0, 5, 0)]
    public async Task SentinelRetryDelayBacksOffDuringAnOutage(int intervalMs, int failures, int expectedMs)
    {
        await Assert.That(ReadEndpointRouter.SentinelRetryDelay(TimeSpan.FromMilliseconds(intervalMs), failures))
            .IsEqualTo(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Test]
    [Arguments("sync")]
    [Arguments("connect")]
    [Arguments("connecting")]
    public async Task ReplicaWithConnectedLinkIsPreferredOverUnlinkedReplica(string linkState)
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var unlinked = new FakeRespServer(UnlinkedRole(linkState))
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk("stale") : null,
        };
        await using var linked = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk("fresh") : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", unlinked.Port), new("127.0.0.1", linked.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        for (var index = 0; index < 4; index++)
            await Assert.That(await view.GetStringAsync($"key-{index}")).IsEqualTo("fresh");

        await Assert.That(unlinked.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task UnlinkedReplicaServesReadsWhenNoLinkedReplicaExists()
    {
        // During a primary outage every replica's link is down. The server's
        // replica-serve-stale-data setting decides whether it answers, not the client.
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = new FakeRespServer(UnlinkedRole("connect"))
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk("stale") : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });

        await Assert.That(await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key")).IsEqualTo("stale");
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    private static byte[] UnlinkedRole(string linkState)
        => Encoding.ASCII.GetBytes(
            $"*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n${linkState.Length}\r\n{linkState}\r\n:-1\r\n");

    [Test]
    public async Task FailedReplicaIsRetriedAfterCooldown()
    {
        var healthy = 0;
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? Volatile.Read(ref healthy) == 1 ? ReplicaRole : PrimaryRole
                : Bulk("replica"),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.ReplicaPreferred);

        await Assert.That(await view.GetStringAsync("a")).IsEqualTo("primary");
        await Assert.That(await view.GetStringAsync("b")).IsEqualTo("primary");
        await Assert.That(replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(1);

        // End the cooldown without depending on wall-clock timing.
        Volatile.Write(ref healthy, 1);
        client.Core.ReadRouter.RefreshInterval = TimeSpan.Zero;
        await Assert.That(await view.GetStringAsync("c")).IsEqualTo("replica");
    }

    [Test]
    public async Task FunctionReloadRetriesOnPrimaryUnderReplicaPolicy()
    {
        const string source = "#!lua name=readlib\nredis.register_function{function_name='readfn', callback=function() return 1 end, flags={'no-writes'}}";
        await using var primary = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("FUNCTION LIST", StringComparison.Ordinal) ? "*0\r\n"u8.ToArray()
                : command.StartsWith("FUNCTION LOAD", StringComparison.Ordinal) ? Bulk("readlib")
                : command.StartsWith("FCALL_RO", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray()
                : null,
        };
        await using var replica = new FakeRespServer(ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("FCALL_RO", StringComparison.Ordinal)
                ? "-ERR Function not found\r\n"u8.ToArray()
                : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var function = RespireFunctionLibrary.Create(source).Function("readfn", readOnly: true);

        using var result = await client.WithReadFrom(RespireReadFrom.Replica).Functions.ExecuteAsync(function);

        // The library was loaded on the primary, so the single retry goes there rather than to a
        // replica that may not have received the load yet.
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("FCALL_RO", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("FUNCTION LOAD", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("FCALL_RO", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task CursorCommandsAreClassifiedForPinning()
    {
        var cursorCommands = RespireCommands.All.ToArray()
            .Where(static command => command.Name is "SCAN" or "HSCAN" or "SSCAN" or "ZSCAN" or "ARSCAN")
            .ToArray();
        await Assert.That(cursorCommands.Length).IsEqualTo(5);
        foreach (var command in cursorCommands)
            await Assert.That(command.ReadKind).IsEqualTo(ReadCommandKind.CursorRead);
        await Assert.That(RespireCommands.String.GET.ReadKind).IsEqualTo(ReadCommandKind.Read);
        await Assert.That(RespireCommands.String.SET.ReadKind).IsEqualTo(ReadCommandKind.None);
        await Assert.That(RespireCommand.Create("CUSTOM.READ").ReadKind).IsEqualTo(ReadCommandKind.None);
    }

    [Test]
    public async Task ReplicaSlotOutageIsReportedWithoutFlushingClientCacheAndClearedOnRetirement()
    {
        await using var server = new FakeRespServer(
            "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$2\r\nab\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            ClientSideCache = new(),
        });
        await Assert.That(await client.Strings.GetRangeAsync("key", 0, 1)).IsEqualTo("ab");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        var replicaEndpoint = new RespireEndpoint("127.0.0.1", 1);
        await using var replica = RespireConnectionMultiplexer.Create(replicaEndpoint.Host, replicaEndpoint.Port);
        var changes = new List<RespireConnectionStateChange>();
        client.Core.ConnectionStateChanged += change => { lock (changes) changes.Add(change); };

        client.Core.NotifyReadReplicaStateChanged(replica, 0,
            new RespireConnectionStateChange(replicaEndpoint, RespireConnectionState.Disconnected, null));
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        client.Core.NotifyReadReplicaRetired(replica);

        RespireConnectionStateChange[] observed;
        lock (changes) observed = [.. changes];
        await Assert.That(observed.Select(change => (change.Endpoint, change.State))).IsEquivalentTo(
            [
                (replicaEndpoint, RespireConnectionState.Disconnected),
                (replicaEndpoint, RespireConnectionState.Connected),
            ]);
        // Positive control: a primary slot outage does flush the cache.
        client.Core.NotifyCommandStateChanged(0, RespireConnectionState.Disconnected);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SentinelOutageKeepsKnownReplicasAndRemovalRetiresReplica()
    {
        int[] replicaPorts = [];
        var outage = 1;
        static byte[]? ReplicaReply(string command)
            => command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk("replica")
                : command.StartsWith("SCAN 0 ", StringComparison.Ordinal) ? KeysPage("7", "a")
                : command.StartsWith("SCAN ", StringComparison.Ordinal) ? KeysPage("0", "b")
                : null;
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? PrimaryRole : FakeRespServer.OkReply,
        };
        await using var first = new FakeRespServer(4, ReplicaRole) { ReplyOverride = (_, command) => ReplicaReply(command) };
        await using var second = new FakeRespServer(4, ReplicaRole) { ReplyOverride = (_, command) => ReplicaReply(command) };
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal)
                ? SentinelAddressReply(primary.Port)
                : command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                    ? Volatile.Read(ref outage) == 1 ? "-ERR sentinel unavailable\r\n"u8.ToArray() : ReplicasReply(Volatile.Read(ref replicaPorts))
                    : "*0\r\n"u8.ToArray(),
        };
        Volatile.Write(ref replicaPorts, [first.Port, second.Port]);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            Connections = 1,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            CommandTimeout = TimeSpan.FromSeconds(10),
            Protocol = RespProtocol.Resp2,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            ReplicaEndpoints = [new("127.0.0.1", first.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        // Configured standalone endpoints are ignored with Sentinel. An outage cannot use them.
        await Assert.That(async () => await view.GetStringAsync("before-discovery"))
            .Throws<RespireConnectionException>();
        Volatile.Write(ref outage, 0);
        await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);

        var enumerator = view.Keys.ScanAsync().GetAsyncEnumerator();
        try
        {
            await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
            var pinned = ScanCursors(first).Length == 1 ? first : second;
            var other = ReferenceEquals(pinned, first) ? second : first;

            // No Sentinel answers: the last known replica set keeps serving reads.
            Volatile.Write(ref outage, 1);
            await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);
            await Assert.That(await view.GetStringAsync("during-outage")).IsEqualTo("replica");

            // An authoritative reply drops the pinned replica from the topology. Reads already in
            // flight when the set changes still complete.
            Volatile.Write(ref outage, 0);
            Volatile.Write(ref replicaPorts, [other.Port]);
            var inFlight = Enumerable.Range(0, 16)
                .Select(index => view.GetStringAsync($"in-flight-{index}").AsTask())
                .ToArray();
            await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);
            var values = await Task.WhenAll(inFlight);
            await Assert.That(values.All(value => value == "replica")).IsTrue();

            await Assert.That(async () => await enumerator.MoveNextAsync()).Throws<RespireConnectionException>();
            await Assert.That(ScanCursors(other)).IsEmpty();
            await Assert.That(await view.GetStringAsync("after-removal")).IsEqualTo("replica");
            await Assert.That(other.ReceivedCommands).Contains("GET after-removal");
            // The removed replica drains and closes without waiting for client disposal.
            await pinned.PeerClosed.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await enumerator.DisposeAsync(); }
    }

    [Test]
    public async Task ReadWithAStaleTopologySnapshotDoesNotRecreateARemovedReplica()
    {
        int[] replicaPorts = [];
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? PrimaryRole : FakeRespServer.OkReply,
        };
        await using var first = new FakeRespServer(4, ReplicaRole);
        await using var second = new FakeRespServer(4, ReplicaRole);
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal)
                ? SentinelAddressReply(primary.Port)
                : command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                    ? ReplicasReply(Volatile.Read(ref replicaPorts))
                    : "*0\r\n"u8.ToArray(),
        };
        Volatile.Write(ref replicaPorts, [first.Port, second.Port]);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            Connections = 1,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            CommandTimeout = TimeSpan.FromSeconds(10),
            Protocol = RespProtocol.Resp2,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        var router = client.Core.ReadRouter;
        await router.RefreshNowAsync(CancellationToken.None);
        RespireEndpoint[] stale = ReadEndpointRouter.Order(
            [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)]);

        // Sentinel drops the first replica. A read that captured the old array before the sweep
        // runs afterwards, which is the sweep-before-insert interleaving.
        Volatile.Write(ref replicaPorts, [second.Port]);
        await router.RefreshNowAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var selection = await router.GetReplicaFromEndpointsAsync(stale, CancellationToken.None);
            await Assert.That(selection.Replica!.Endpoint.Port).IsEqualTo(second.Port);
            await Assert.That(router.IsCurrent(selection.Replica)).IsTrue();
        }

        await Assert.That(first.ReceivedCommands).DoesNotContain("ROLE");
        await Assert.That(router.GetOpenEndpoints().Select(static endpoint => endpoint.Port)).DoesNotContain(first.Port);
    }

    [Test]
    public async Task RemovedReplicaFinishesAStreamedReadBeforeClosing()
    {
        const int payloadLength = 128 * 1024;
        var frame = new byte[payloadLength + 32];
        var header = Encoding.ASCII.GetBytes($"${payloadLength}\r\n");
        header.CopyTo(frame, 0);
        frame.AsSpan(header.Length, payloadLength).Fill((byte)'x');
        "\r\n"u8.CopyTo(frame.AsSpan(header.Length + payloadLength));
        frame = frame[..(header.Length + payloadLength + 2)];
        int[] replicaPorts = [];
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? PrimaryRole : FakeRespServer.OkReply,
        };
        await using var first = new FakeRespServer(4, ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? frame : null,
        };
        await using var second = new FakeRespServer(4, ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? frame : null,
        };
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal)
                ? SentinelAddressReply(primary.Port)
                : command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                    ? ReplicasReply(Volatile.Read(ref replicaPorts))
                    : "*0\r\n"u8.ToArray(),
        };
        Volatile.Write(ref replicaPorts, [first.Port, second.Port]);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            Connections = 1,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // Far shorter than the time the consumer below holds the stream open.
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            Protocol = RespProtocol.Resp2,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        client.Core.ReadRouter.RetiredStreamIdleLimit = TimeSpan.FromSeconds(30);
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        await using var stream = await view.Strings.GetStreamAsync("big");
        var buffer = new byte[2 * 1024];
        var total = await stream!.ReadAsync(buffer);
        var serving = first.ReceivedCommands.Contains("GET big") ? first : second;
        var other = ReferenceEquals(serving, first) ? second : first;

        Volatile.Write(ref replicaPorts, [other.Port]);
        await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        await Assert.That(total).IsEqualTo(payloadLength);
        // Once the stream completes, the drained replica closes without waiting for client disposal.
        await serving.PeerClosed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task RemovedReplicaClosesWhenAStreamedReadStopsMakingProgress()
    {
        const int payloadLength = 8 * 1024 * 1024;
        var frame = new byte[payloadLength + 32];
        var header = Encoding.ASCII.GetBytes($"${payloadLength}\r\n");
        header.CopyTo(frame, 0);
        frame.AsSpan(header.Length, payloadLength).Fill((byte)'x');
        "\r\n"u8.CopyTo(frame.AsSpan(header.Length + payloadLength));
        frame = frame[..(header.Length + payloadLength + 2)];
        int[] replicaPorts = [];
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? PrimaryRole : FakeRespServer.OkReply,
        };
        await using var first = new FakeRespServer(4, ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? frame : null,
        };
        await using var second = new FakeRespServer(4, ReplicaRole)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? frame : null,
        };
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal)
                ? SentinelAddressReply(primary.Port)
                : command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                    ? ReplicasReply(Volatile.Read(ref replicaPorts))
                    : "*0\r\n"u8.ToArray(),
        };
        Volatile.Write(ref replicaPorts, [first.Port, second.Port]);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            Connections = 1,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            Protocol = RespProtocol.Resp2,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        client.Core.ReadRouter.RetiredStreamIdleLimit = TimeSpan.FromMilliseconds(300);
        var view = client.WithReadFrom(RespireReadFrom.Replica);

        // The caller reads one chunk, then abandons the stream without disposing it.
        var stream = await view.Strings.GetStreamAsync("big");
        var buffer = new byte[64 * 1024];
        var total = await stream!.ReadAsync(buffer);
        var serving = first.ReceivedCommands.Contains("GET big") ? first : second;
        var other = ReferenceEquals(serving, first) ? second : first;

        Volatile.Write(ref replicaPorts, [other.Port]);
        await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);

        // The stalled stream no longer holds the removed replica open until client disposal.
        await serving.PeerClosed.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0) total += read;
        }
        catch (Exception) { }
        await Assert.That(total).IsLessThan(payloadLength);
        await stream.DisposeAsync();
    }

    [Test]
    public async Task ClientDisposalDoesNotWaitForAReplicaRoleCheck()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole) { SuppressReply = command => command == "ROLE" };
        var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ConnectTimeout = TimeSpan.FromMinutes(1),
            CommandTimeout = TimeSpan.FromMinutes(1),
        });

        var read = client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key").AsTask();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!replica.ReceivedCommands.Contains("ROLE") && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Assert.That(replica.ReceivedCommands).Contains("ROLE");

        // The ROLE check holds the replica entry's gate; disposal cancels it instead of waiting
        // for the one-minute command timeout.
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(async () => await read).Throws<Exception>();
    }

    [Test]
    public async Task SentinelReplicaDiscoveryUsesOneSentinelsViewInsteadOfMergingThem()
    {
        await using var current = SentinelReplying(ReplicasReply([7001]));
        await using var stale = SentinelReplying(ReplicasReply([7001, 7002]));

        var replicas = await SentinelResolver.DiscoverReplicaEndpointsAsync(
            DiscoveryOptions(), [new("127.0.0.1", current.Port), new("127.0.0.1", stale.Port)], CancellationToken.None);

        // 7002 is listed only by the second Sentinel, which is never consulted once the first answers.
        await Assert.That(replicas).IsEquivalentTo([new RespireEndpoint("127.0.0.1", 7001)]);
        await Assert.That(stale.ReceivedCommands.Any(command => command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task SentinelReplicaDiscoverySkipsAMalformedReplyAndAcceptsAnEmptyOne()
    {
        await using var malformed = SentinelReplying("*1\r\n:5\r\n"u8.ToArray());
        await using var empty = SentinelReplying("*0\r\n"u8.ToArray());
        await using var unused = SentinelReplying(ReplicasReply([7001]));

        // The malformed reply counts as a failed Sentinel; the empty array is authoritative.
        var replicas = await SentinelResolver.DiscoverReplicaEndpointsAsync(
            DiscoveryOptions(),
            [new("127.0.0.1", malformed.Port), new("127.0.0.1", empty.Port), new("127.0.0.1", unused.Port)],
            CancellationToken.None);
        await Assert.That(replicas).IsEmpty();
        await Assert.That(unused.ReceivedCommands.Any(command => command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)))
            .IsFalse();

        // Only malformed replies: discovery fails, so the router keeps its last known replicas.
        await using var alsoMalformed = SentinelReplying("*1\r\n*2\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n"u8.ToArray());
        await Assert.That(async () => await SentinelResolver.DiscoverReplicaEndpointsAsync(
                DiscoveryOptions(), [new("127.0.0.1", malformed.Port), new("127.0.0.1", alsoMalformed.Port)],
                CancellationToken.None))
            .ThrowsExactly<RespireConnectionException>();
    }

    [Test]
    [Arguments("*0\r\n", true, "")]
    [Arguments("*1\r\n*6\r\n$2\r\nip\r\n$2\r\nh1\r\n$4\r\nport\r\n$4\r\n7001\r\n$5\r\nflags\r\n$5\r\nslave\r\n", true, "h1:7001")]
    [Arguments("*1\r\n*6\r\n$2\r\nip\r\n$2\r\nh1\r\n$4\r\nport\r\n$4\r\n7001\r\n$5\r\nflags\r\n$12\r\nslave,s_down\r\n", true, "")]
    [Arguments("*1\r\n*6\r\n$2\r\nip\r\n$2\r\nh1\r\n$4\r\nport\r\n$4\r\n7001\r\n$9\r\nlink-refc\r\n:3\r\n", true, "h1:7001")]
    [Arguments("*1\r\n*4\r\n$2\r\nip\r\n$2\r\nh1\r\n$4\r\nport\r\n$1\r\nx\r\n", false, "")]
    [Arguments("*1\r\n*3\r\n$2\r\nip\r\n$2\r\nh1\r\n$4\r\nport\r\n", false, "")]
    [Arguments("*1\r\n*4\r\n$2\r\nip\r\n:1\r\n$4\r\nport\r\n$4\r\n7001\r\n", false, "")]
    [Arguments("*1\r\n:5\r\n", false, "")]
    [Arguments("-ERR no such master\r\n", false, "")]
    [Arguments(":1\r\n", false, "")]
    public async Task SentinelReplicaListParsing(string wire, bool expected, string endpoints)
    {
        var parsed = SentinelResolver.TryParseReplicaList(Parse(wire), out var replicas);

        await Assert.That(parsed).IsEqualTo(expected);
        await Assert.That(string.Join(",", replicas.Select(endpoint => $"{endpoint.Host}:{endpoint.Port}")))
            .IsEqualTo(endpoints);
    }

    private static FakeRespServer SentinelReplying(byte[] replicas)
        => new(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                ? replicas : FakeRespServer.OkReply,
        };

    private static RespireOptions DiscoveryOptions() => new()
    {
        SentinelPrimaryName = "mymaster",
        Protocol = RespProtocol.Resp2,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        CommandTimeout = TimeSpan.FromSeconds(10),
    };

    private static RespValue Parse(string wire)
    {
        var position = 0;
        if (RespParser.TryParseValue(Encoding.UTF8.GetBytes(wire), ref position, out var value) != RespParseStatus.Done)
            throw new InvalidOperationException("Invalid test frame.");
        return value;
    }

    private static byte[] SentinelAddressReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");

    private static byte[] ReplicasReply(int[] ports)
    {
        var reply = new StringBuilder($"*{ports.Length}\r\n");
        foreach (var port in ports)
        {
            var text = port.ToString();
            reply.Append($"*6\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n${text.Length}\r\n{text}\r\n$5\r\nflags\r\n$5\r\nslave\r\n");
        }
        return Encoding.ASCII.GetBytes(reply.ToString());
    }

    private static string[] ScanCursors(FakeRespServer server)
        => server.ReceivedCommands
            .Where(command => command.StartsWith("SCAN ", StringComparison.Ordinal))
            .Select(command => command.Split(' ')[1])
            .ToArray();

    private static byte[] KeysPage(string cursor, string key)
        => Encoding.ASCII.GetBytes($"*2\r\n${cursor.Length}\r\n{cursor}\r\n*1\r\n${key.Length}\r\n{key}\r\n");

    private static byte[] ScanReply(string cursor)
        => Encoding.ASCII.GetBytes($"*2\r\n${cursor.Length}\r\n{cursor}\r\n*0\r\n");

    private static byte[] Bulk(string value)
        => Encoding.ASCII.GetBytes($"${Encoding.ASCII.GetByteCount(value)}\r\n{value}\r\n");
}
