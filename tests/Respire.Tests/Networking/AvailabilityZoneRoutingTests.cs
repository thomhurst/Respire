using System.Text;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[ParallelLimiter<TimingSensitive>]
public class AvailabilityZoneRoutingTests
{
    [Test]
    [Arguments(false, RespireReadFrom.AzAffinity, false)]
    [Arguments(false, RespireReadFrom.AzAffinity, true)]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, false)]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, true)]
    [Arguments(true, RespireReadFrom.AzAffinity, false)]
    [Arguments(true, RespireReadFrom.AzAffinity, true)]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, false)]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, true)]
    public async Task HedgeExcludesOriginalPeerAndPreservesZoneRanking(
        bool cluster, RespireReadFrom policy, bool localAlternative)
    {
        await using var primary = Node("primary", "local", false);
        await using var original = Node("original", "local", true);
        await using var remote = Node("remote", "remote", true);
        await using var local = Node("local", "local", true);
        var replicas = localAlternative ? new[] { original, remote, local } : [original, remote];
        ConfigureTopology(primary, replicas);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replicas, cluster, policy));
#pragma warning restore RESP010
#pragma warning disable RESP010 // This separately owned client supplies the original hedge peer whose endpoint must be excluded.
        await using var originalClient = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", original.Port)], Protocol = RespireProtocol.Resp2, Connections = 1,
        });
#pragma warning restore RESP010
        var originalConnection = originalClient.Core.Multiplexer.GetConnection();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selected = cluster
            ? await client.Core.Cluster!.GetHedgeConnectionAsync(ClusterHash.GetSlot("key"), policy, originalConnection, limit.Token)
            : await client.Core.ReadRouter.GetHedgeConnectionAsync(policy, originalConnection, limit.Token);
        var expected = localAlternative ? local : policy == RespireReadFrom.AzAffinity ? remote : primary;
        await Assert.That(selected?.Port).IsEqualTo(expected.Port);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(4, false)]
    [Arguments(5, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    [Arguments(4, true)]
    [Arguments(5, true)]
    [Arguments(6, false)]
    [Arguments(6, true)]
    public async Task TransportRetirementKeepsOriginalZone(int mode, bool waitForCapacity)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "local", true);
        ConfigureMixedSocketZones(replica);
        replica.SuppressReply = command => command == "PING";
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false,
            RespireReadFrom.AzAffinity) with { Connections = 4, MaxInflightCommands = 4 });
#pragma warning restore RESP010
        var selected = await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.AzAffinity, default, observation: default);
        var owner = selected.Multiplexer!;
        var remote = Enumerable.Range(0, 4).Select(owner.GetConnection)
            .First(connection => connection.AvailabilityZone == "remote");
        var accepted = new List<Task<Respire.Protocol.RespValue>>();
        if (waitForCapacity)
        {
            for (var index = 0; index < 4; index++)
                accepted.Add(selected.SendAsync(new Respire.Commands.RawCommand(FakeRespServer.PingFrame)).AsTask());
        }
        else await selected.RetireAsync();
        // Model the post-selection MOVING boundary. The next ordinary selection picks a
        // remote socket, while another local socket remains eligible on the same owner.
        typeof(Respire.Infrastructure.RespireConnectionMultiplexer)
            .GetField("_next", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(owner, unchecked((uint)(remote.MultiplexerSlot - 1)));
        var command = new Respire.Commands.Cmd1(Respire.Commands.Verbs.Get, "key");
        var read = ReadAsync();
        Task retirement = Task.CompletedTask;
        try
        {
            if (waitForCapacity)
            {
                await Assert.That(read.IsCompleted).IsFalse();
                retirement = selected.RetireAsync();
            }
            await Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("local");
            await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
        }
        finally
        {
            if (waitForCapacity)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (replica.ReceivedCommands.Count(command => command == "PING") < 4)
                    await Task.Delay(10, limit.Token);
                var index = replica.ReceivedCommands.ToList().FindIndex(command => command == "PING");
                await replica.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(),
                    replica.ReceivedConnectionIds[index]);
                foreach (var pending in accepted) (await pending.WaitAsync(limit.Token)).Dispose();
                await retirement.WaitAsync(limit.Token);
            }
        }

        async Task<string?> ReadAsync()
        {
            if (mode == 6)
            {
                await selected.SendFireAndForgetAsync(command, preferredZone: "local");
                return await ObservedReadZoneAsync(replica);
            }
            if (mode is 2 or 3)
            {
                await using var stream = mode == 2
                    ? await selected.SendBulkStreamAsync(command, preferredZone: "local")
                    : await ClusterRouter.SendAskingBulkStreamAsync(selected, command, default, preferredZone: "local");
                using var reader = new StreamReader(stream!);
                return await reader.ReadToEndAsync();
            }
            var caching = new Respire.Commands.ClientCachingCommand();
            using var reply = mode switch
            {
                4 => await selected.SendValidatedPrefixedAsync(caching, command, preferredZone: "local"),
                5 => await ClusterRouter.SendTrackedAskingAsync(selected, command, default, preferredZone: "local"),
                _ => await client.SendOnConnectionAsync("GET", selected, command, default, sendAsking: mode == 1),
            };
            return reply.AsString();
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PublicReadKeepsZoneWhenSocketRetiresBeforeAdmission(int mode)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "local", true);
        ConfigureMixedSocketZones(replica);
        if (mode == 1) ConfigureTopology(primary, replica);
        var slot = ClusterHash.GetSlot("key");
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], mode == 1,
            RespireReadFrom.AzAffinity) with { Connections = 4 });
#pragma warning restore RESP010
        var selected = mode == 1
            ? await client.Core.Cluster!.GetReadConnectionAsync(slot, RespireReadFrom.AzAffinity, default, observation: default)
            : await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.AzAffinity, default, observation: default);
        var owner = selected.Multiplexer!;
        var remote = Enumerable.Range(0, 4).Select(owner.GetConnection)
            .First(connection => connection.AvailabilityZone == "remote");
        var cursor = typeof(Respire.Infrastructure.RespireConnectionMultiplexer)
            .GetField("_next", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var retired = 0;
        Task retirement = Task.CompletedTask;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "GET" || activity.GetTagItem("server.port") is not int port
                    || port != replica.Port || Interlocked.CompareExchange(ref retired, 1, 0) != 0) return;
                // Telemetry starts after endpoint/socket selection and before enqueue. Reproduce
                // that exact retirement boundary without changing the live multiplexer owner.
                var current = owner.GetConnectionForZone("local",
                    mode == 1 ? slot : unchecked((int)(uint)cursor.GetValue(owner)!));
                retirement = current.RetireAsync();
                cursor.SetValue(owner, unchecked((uint)(remote.MultiplexerSlot - 1)));
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        string? result;
        if (mode == 1)
        {
            using var batch = client.CreateBatch();
            var pending = batch.Strings.GetString("key");
            await batch.ExecuteAsync();
            result = await pending;
        }
        else if (mode == 2)
        {
            await using var stream = await client.Strings.GetStreamAsync("key");
            using var reader = new StreamReader(stream!);
            result = await reader.ReadToEndAsync();
        }
        else if (mode == 3)
        {
            await client.ExecuteFireAndForgetAsync(RespireCommands.String.GET, ["key"]);
            result = await ObservedReadZoneAsync(replica);
        }
        else result = await client.GetStringAsync("key");
        await Assert.That(result).IsEqualTo("local");
        await Assert.That(retired).IsEqualTo(1);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    [Arguments(true)]
    [Arguments(false)]
    public async Task WarmZoneSelectionAndCounterAllocateNothing(bool local)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", local ? "local" : "remote", true);
        await using var second = Node("second", local ? "local" : "remote", true);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica, second], false, RespireReadFrom.AzAffinity)
            with { ReplicaRefreshInterval = TimeSpan.FromMinutes(5) });
#pragma warning restore RESP010
        var router = client.Core.ReadRouter!;
        // Ready-only public dispatch may use a fallback while a cold candidate validates.
        // Prepare each endpoint explicitly before measuring the synchronous selector.
        foreach (var server in new[] { replica, second })
            await router.GetReplicaFromEndpointsAsync([new("127.0.0.1", server.Port)], default, default);
        var counter = AvailabilityZoneTelemetry.ForZone("local");
        MeasureSelection(router, counter, false);
        MeasureSelection(router, counter, true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Actual: MeasureSelection(router, counter, false), Control: MeasureSelection(router, counter, true)));
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureSelection(ReadEndpointRouter router, AvailabilityZoneTelemetry.Counter counter, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            var selected = router.SelectAsync(RespireReadFrom.AzAffinity, CancellationToken.None, observation: default);
            if (!selected.IsCompletedSuccessfully) throw new InvalidOperationException("Warm selection must complete synchronously.");
#pragma warning disable RESP009 // IsCompletedSuccessfully above guarantees this allocation probe cannot block or leave the measuring thread.
            _ = selected.Result;
#pragma warning restore RESP009
            counter.Increment();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task ReconnectedPhysicalConnectionRefreshesZone()
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "local", true);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, RespireReadFrom.AzAffinity));
#pragma warning restore RESP010
        var old = (await client.Core.ReadRouter!.SelectAsync(RespireReadFrom.AzAffinity, CancellationToken.None, observation: default)).Connection;
        await Assert.That(old.AvailabilityZone).IsEqualTo("local");
        var previous = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk("availability_zone:changed\r\n") : previous(id, command);
        replica.CloseConnections();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (old.IsAcceptingCommands) await Task.Delay(5, timeout.Token);
        old.Multiplexer!.ScheduleReconnect(0);
        while (!old.Multiplexer!.HasConnection(connection => connection.IsAcceptingCommands
            && !ReferenceEquals(connection, old))) await Task.Delay(5, timeout.Token);
        var current = (await client.Core.ReadRouter.SelectAsync(RespireReadFrom.Replica, timeout.Token, observation: default)).Connection;
        await Assert.That(current.AvailabilityZone).IsEqualTo("changed");
    }

    [Test]
    // One ordering table covers both routers and policies: local replica, eligible local
    // primary, then remote replica. A remote primary never outranks a healthy replica.
    [Arguments(false, RespireReadFrom.AzAffinity, true, true, "local")]
    [Arguments(true, RespireReadFrom.AzAffinity, true, true, "local")]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, true, true, "local")]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, true, true, "local")]
    [Arguments(false, RespireReadFrom.AzAffinity, true, false, "local")]
    [Arguments(true, RespireReadFrom.AzAffinity, true, false, "local")]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, true, false, "local")]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, true, false, "local")]
    [Arguments(false, RespireReadFrom.AzAffinity, false, true, "remote")]
    [Arguments(true, RespireReadFrom.AzAffinity, false, true, "remote")]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, false, true, "primary")]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, false, true, "primary")]
    [Arguments(false, RespireReadFrom.AzAffinity, false, false, "remote")]
    [Arguments(true, RespireReadFrom.AzAffinity, false, false, "remote")]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, false, false, "remote")]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, false, false, "remote")]
    public async Task ZoneFallbackOrderingKeepsWritesPrimary(bool cluster, RespireReadFrom policy,
        bool includeLocalReplica, bool localPrimary, string expected)
    {
        await using var primary = Node("primary", localPrimary ? "local" : "remote", false);
        await using var local = Node("local", "local", true);
        await using var remote = Node("remote", "remote", true);
        FakeRespServer[] replicas = includeLocalReplica ? [local, remote] : [remote];
        ConfigureTopology(primary, replicas);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, replicas, cluster, policy));
#pragma warning restore RESP010
        for (var index = 0; index < 8; index++)
            await Assert.That(await client.GetStringAsync("{zone}:key")).IsEqualTo(expected);
        using (var reply = await client.ExecuteAsync(RespireCommands.String.GET, "{zone}:key"))
            await Assert.That(reply.AsString()).IsEqualTo(expected);
        await Assert.That(await client.SetAsync("{zone}:key", "value")).IsTrue();
        await Assert.That(primary.ReceivedCommands.Contains("SET {zone}:key value")).IsTrue();
        foreach (var (node, name) in new[] { (primary, "primary"), (local, "local"), (remote, "remote") })
            await Assert.That(node.ReceivedCommands.Count(command => command.StartsWith("GET ")))
                .IsEqualTo(name == expected ? 9 : 0);
        await Assert.That(local.ReceivedCommands.Any(command => command.StartsWith("SET "))).IsFalse();
        await Assert.That(remote.ReceivedCommands.Any(command => command.StartsWith("SET "))).IsFalse();
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(true, false)]
    public async Task MixedZoneSocketsSelectTheLocalPhysicalConnection(bool cluster, bool mixedPrimary)
    {
        await using var primary = Node("primary", mixedPrimary ? "remote" : "local", false);
        await using var replica = Node("replica", "remote", true);
        ConfigureTopology(primary, replica);
        var mixed = mixedPrimary ? primary : replica;
        var original = mixed.ReplyOverride!;
        mixed.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n")
            : command.StartsWith("GET ") ? Bulk(id % 2 == 0 ? "local-socket" : "remote-socket")
            : original(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
#pragma warning restore RESP010
        for (var index = 0; index < 8; index++)
            await Assert.That(await client.GetStringAsync($"{{zone-{index}}}:key")).IsEqualTo("local-socket");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetiredFallbackDoesNotHideAnotherValidatedReplica(bool laterProbeFails)
    {
        var disconnected = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$12\r\ndisconnected\r\n:0\r\n"u8.ToArray();
        await using var primary = Node("primary", "remote", false);
        await using var first = Node("first", "local", true);
        await using var second = Node("second", "local", true);
        await using var third = Node("third", "local", true);
        foreach (var server in new[] { first, second, third })
        {
            var original = server.ReplyOverride!;
            server.ReplyOverride = (id, command) => command == "ROLE" ? disconnected : original(id, command);
        }
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = laterProbeFails ? third : second;
        blocked.SuppressReply = command =>
        {
            if (command != "ROLE") return false;
            reached.TrySetResult();
            return true;
        };
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [first, second, third], false,
            RespireReadFrom.AzAffinity));
#pragma warning restore RESP010
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var router = client.Core.ReadRouter;
        var old = await router.GetReplicaFromEndpointsAsync([new("127.0.0.1", first.Port)], timeout.Token, default,
            RespireReadFrom.AzAffinity);
        // The warm lookup increments rotation once; start the next lookup at first in either array.
        RespireEndpoint[] endpoints = laterProbeFails
            ? [new("127.0.0.1", second.Port), new("127.0.0.1", third.Port), new("127.0.0.1", first.Port)]
            : [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)];
        var selection = router.GetReplicaFromEndpointsAsync(endpoints, timeout.Token, default, RespireReadFrom.AzAffinity).AsTask();
        await reached.Task.WaitAsync(timeout.Token);
        first.CloseConnections();
        while (old.Connection.IsAcceptingCommands) await Task.Delay(5, timeout.Token);
        await blocked.SendRawAsync(laterProbeFails ? "-LOADING unavailable\r\n"u8.ToArray() : disconnected,
            blocked.ReceivedConnectionIds[^1]);
        var selected = await selection.WaitAsync(timeout.Token);
        await Assert.That(selected.Connection.Port).IsEqualTo(second.Port);
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    [Arguments(RespireProtocol.Auto)]
    public async Task PhysicalConnectionCapturesServerZone(RespireProtocol protocol)
    {
        await using var server = Node("primary", "zone-a", false);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = protocol, DiscoverAvailabilityZone = true });
        await Assert.That(connection.AvailabilityZone).IsEqualTo("zone-a");
        await Assert.That(server.ReceivedCommands.Contains("INFO SERVER")).IsEqualTo(protocol == RespireProtocol.Resp2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnavailableReplicaFallsBackToPrimary(bool cluster)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "local", true);
        ConfigureTopology(primary, replica);
        // Keep the port reserved; a disposed listener's port can be reused by a parallel test.
        var previous = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command is "ROLE" or "READONLY"
            ? "-LOADING unavailable\r\n"u8.ToArray() : previous(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster, RespireReadFrom.AzAffinity));
#pragma warning restore RESP010
        await Assert.That(await client.GetStringAsync("{zone}:key")).IsEqualTo("primary");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownZonesRemainEligibleForReplicaFallback(bool cluster)
    {
        await using var primary = Node("primary", null, false);
        await using var replica = Node("replica", null, true);
        ConfigureTopology(primary, replica);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster, RespireReadFrom.AzAffinityReplicasAndPrimary));
#pragma warning restore RESP010
        await Assert.That(await client.GetStringAsync("{zone}:key")).IsEqualTo("replica");
    }

    [Test]
    public async Task ClusterLocalPrimaryLoadingFallsBackToRemoteReplica()
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "remote", true);
        ConfigureTopology(primary, replica);
        var previous = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command.StartsWith("GET ")
            ? "-LOADING unavailable\r\n"u8.ToArray() : previous(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true, RespireReadFrom.AzAffinityReplicasAndPrimary));
#pragma warning restore RESP010
        await Assert.That(await client.GetStringAsync("{zone}:key")).IsEqualTo("replica");
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    [Arguments("LOADING")]
    [Arguments("MASTERDOWN")]
    [Arguments("CLUSTERDOWN")]
    public async Task ClusterReplicaRejectionPreservesLocalPrimarySocket(string error)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "local", true);
        ConfigureTopology(primary, replica);
        var originalPrimary = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n")
            : command.StartsWith("GET ") ? Bulk(id % 2 == 0 ? "local-socket" : "remote-socket")
            : originalPrimary(id, command);
        var originalReplica = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command.StartsWith("GET ")
            ? Encoding.UTF8.GetBytes($"-{error} unavailable\r\n") : originalReplica(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
#pragma warning restore RESP010
        for (var index = 0; index < 8; index++)
            await Assert.That(await client.GetStringAsync($"{{zone-{index}}}:key")).IsEqualTo("local-socket");
    }

    [Test]
    public async Task ClusterPrimaryRejectionPreservesReplicaZoneWithoutRetryingPrimary()
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "remote", true);
        ConfigureTopology(primary, replica);
        var original = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n") : original(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
#pragma warning restore RESP010
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null, observation: default);
        for (var slot = 0; slot < 8; slot++)
        {
            var fallback = new ReadFallbackPolicy.RoleFallback(RespireReadFrom.AzAffinityReplicasAndPrimary);
            await Assert.That(fallback.TrySwitch(new RespireServerException("LOADING unavailable"), onReplica: false)).IsTrue();
            var selected = await router.GetOtherRoleReadConnectionAsync(slot,
                fallback, CancellationToken.None, discovery: null, observation: default);
            await Assert.That(selected.Port).IsEqualTo(replica.Port);
            await Assert.That(selected.AvailabilityZone).IsEqualTo("local");
        }
    }

    [Test]
    [Arguments(RespireReadFrom.Primary)]
    [Arguments(RespireReadFrom.Replica)]
    [Arguments(RespireReadFrom.AzAffinity)]
    public async Task AcceptedReadsAreCountedByZoneWithoutCountingWrites(RespireReadFrom policy)
    {
        var zone = $"telemetry-zone-{policy}";
        await using var primary = Node("primary", zone, false);
        await using var replica = Node("replica", zone, true);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, policy)
            with { ClientAvailabilityZone = zone });
#pragma warning restore RESP010
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Name == "respire.read.availability_zone") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.availability_zone" && Equals(tag.Value, zone)) observed = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        var baseline = observed;
        await client.GetStringAsync("{zone}:key");
        await client.SetAsync("{zone}:key", "value");
        using (var reply = await client.ExecuteAsync(RespireCommands.String.GET, "{zone}:key")) { }
        listener.RecordObservableInstruments();
        await Assert.That(observed - baseline).IsEqualTo(2);
    }

    [Test]
    public async Task ZoneObservationSeesNewMembershipAndLiveCounterValues()
    {
        const string zone = "telemetry-snapshot-membership";
        long observed = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Name == "respire.read.availability_zone") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.availability_zone" && Equals(tag.Value, zone)) observed = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        await Assert.That(observed).IsEqualTo(-1L);
        var counter = AvailabilityZoneTelemetry.ForZone(zone);
        counter.Increment();
        listener.RecordObservableInstruments();
        await Assert.That(observed).IsEqualTo(1L);
        await Assert.That(AvailabilityZoneTelemetry.ForZone(zone)).IsSameReferenceAs(counter);
        counter.Increment();
        listener.RecordObservableInstruments();
        await Assert.That(observed).IsEqualTo(2L);
    }

    [Test]
    public async Task ZoneLookupAclErrorDoesNotPreventResp2Connection()
    {
        await using var server = Node("primary", null, false);
        var previous = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? "-NOPERM INFO denied\r\n"u8.ToArray() : previous(id, command);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespireProtocol.Resp2, DiscoverAvailabilityZone = true, Database = 1 });
        await Assert.That(connection.AvailabilityZone).IsNull();
        await Assert.That(server.ReceivedCommands.Contains("SELECT 1")).IsTrue();
    }

    [Test]
    public async Task MissingZoneConfigurationRejectsAffinityPolicyAndView()
    {
        await Assert.That(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 6379)], ReplicaEndpoints = [new("127.0.0.1", 6380)],
            ReadFrom = RespireReadFrom.AzAffinity,
        })).Throws<RespireConfigurationException>();
#pragma warning disable RESP010 // This test owns a lazy client only to verify that a zone-affinity view rejects missing zone configuration without connecting.
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 6379)], ReplicaEndpoints = [new("127.0.0.1", 6380)],
        });
#pragma warning restore RESP010
        await Assert.That(() => client.WithReadFrom(RespireReadFrom.AzAffinityReplicasAndPrimary))
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(RespireReadFrom.AzAffinity, true, "local", "remote")]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true, "local", "primary")]
    [Arguments(RespireReadFrom.AzAffinity, false, "local", "unlinked")]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, false, "local", "primary")]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true, "remote", "remote")]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, false, "remote", "unlinked")]
    public async Task UnlinkedLocalReplicaFollowsHealthyCandidates(
        RespireReadFrom policy, bool includeRemote, string primaryZone, string expected)
    {
        await using var primary = Node("primary", primaryZone, false);
        await using var local = Node("unlinked", "local", true);
        await using var remote = Node("remote", "remote", true);
        var previous = local.ReplyOverride!;
        local.ReplyOverride = (id, command) => command == "ROLE"
            ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$12\r\ndisconnected\r\n:0\r\n"u8.ToArray()
            : previous(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary,
            includeRemote ? [local, remote] : [local], false, policy));
#pragma warning restore RESP010
        for (var index = 0; index < 4; index++)
            await Assert.That(await client.GetStringAsync("key")).IsEqualTo(expected);
    }

    [Test]
    [Arguments(RespireReadFrom.AzAffinity)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task PrimarySelectionFallbackDoesNotBounceAfterTwoLoadingReplies(RespireReadFrom policy)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "remote", true);
        ConfigureTopology(primary, replica);
        var ready = 0;
        var primaryReply = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) =>
        {
            if (!command.StartsWith("GET ")) return primaryReply(id, command);
            Volatile.Write(ref ready, 1);
            return "-LOADING unavailable\r\n"u8.ToArray();
        };
        var replicaReply = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command.StartsWith("GET ")
            || command == "READONLY" && Volatile.Read(ref ready) == 0
                ? "-LOADING unavailable\r\n"u8.ToArray() : replicaReply(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true, policy));
#pragma warning restore RESP010
        await Assert.That(async () => await client.GetStringAsync("{zone}:key")).Throws<RespireServerException>();
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    public async Task PrimaryZoneProbeUsesPhysicalConnectionMetadata()
    {
        await using var primary = Node("primary", "remote", false);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], false, RespireReadFrom.Primary));
#pragma warning restore RESP010
        var multiplexer = client.Core.Multiplexer;
        await Assert.That(ReadFallbackPolicy.ShouldProbeLocalPrimary(
            RespireReadFrom.AzAffinityReplicasAndPrimary, multiplexer, "local")).IsFalse();
        await Assert.That(ReadFallbackPolicy.ShouldProbeLocalPrimary(
            RespireReadFrom.AzAffinityReplicasAndPrimary, multiplexer, "remote")).IsTrue();
        await Assert.That(ReadFallbackPolicy.ShouldProbeLocalPrimary(
            RespireReadFrom.AzAffinityReplicasAndPrimary, null, "local")).IsTrue();
        await Assert.That(ReadFallbackPolicy.ShouldProbeLocalPrimary(
            RespireReadFrom.AzAffinity, multiplexer, "remote")).IsFalse();
        var previous = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk("availability_zone:local\r\n") : previous(id, command);
        var old = multiplexer.GetConnection();
        primary.CloseConnections();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (old.IsAcceptingCommands) await Task.Delay(5, timeout.Token);
        await Assert.That(multiplexer.MayBeInAvailabilityZone("local")).IsFalse();
        multiplexer.ScheduleReconnect(0);
        while (!multiplexer.HasConnection(connection => !ReferenceEquals(connection, old)))
            await Task.Delay(5, timeout.Token);
        await Assert.That(multiplexer.MayBeInAvailabilityZone("local")).IsTrue();
    }

    [Test]
    public async Task SentinelWithoutReplicasKeepsLocalPrimarySocket()
    {
        await using var primary = Node("primary", "remote", false);
        var original = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n")
            : command.StartsWith("GET ") ? Bulk(id % 2 == 0 ? "local" : "remote") : original(id, command);
        await using var sentinel = new FakeRespServer(16, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${primary.Port.ToString().Length}\r\n{primary.Port}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], false,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2, SentinelPrimaryName = "primary" });
#pragma warning restore RESP010
        for (var index = 0; index < 8; index++)
            await Assert.That(await client.GetStringAsync("key")).IsEqualTo("local");
    }

    [Test]
    [Arguments(false, false, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(false, false, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(false, true, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(false, true, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(true, false, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(true, false, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(true, true, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(true, true, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(false, false, false, RespireReadFrom.AzAffinity)]
    [Arguments(false, true, false, RespireReadFrom.AzAffinity)]
    [Arguments(true, false, false, RespireReadFrom.AzAffinity)]
    [Arguments(true, true, false, RespireReadFrom.AzAffinity)]
    public async Task PinnedCursorKeepsLocalPhysicalSocket(bool cluster, bool shared, bool primaryPin, RespireReadFrom policy)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "remote", true);
        ConfigureTopology(primary, replica);
        var mixed = primaryPin ? primary : replica;
        var original = mixed.ReplyOverride!;
        mixed.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n") : original(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster, policy)
            with { Connections = 2 });
#pragma warning restore RESP010
        ReadAffinity? pin = shared ? null : new();
        var cursors = client.Core.ReadRouter.Cursors;
        for (var page = 0; page < 8; page++)
        {
            var selected = cluster
                ? await cursors.GetClusterConnectionAsync(client.Core.Cluster!, 1, policy, pin, page > 0, CancellationToken.None, observation: default)
                : await cursors.GetConnectionAsync(client.Core.ReadRouter, policy, pin, page > 0, CancellationToken.None, observation: default);
            await Assert.That(selected.Port).IsEqualTo(mixed.Port);
            await Assert.That(selected.AvailabilityZone).IsEqualTo("local");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingClusterReadPrefersLocalIdleLease(bool roleFallback)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", roleFallback ? "local" : "remote", true);
        ConfigureTopology(primary, replica);
        var primaryReply = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n")
            : command.StartsWith("XREAD ") ? Bulk(id % 2 == 0 ? "local" : "remote") : primaryReply(id, command);
        var replicaReply = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command.StartsWith("XREAD ")
            ? "-LOADING unavailable\r\n"u8.ToArray() : replicaReply(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
#pragma warning restore RESP010
        var pool = await client.Core.Cluster!.GetReadDedicatedPoolAsync(ClusterHash.GetSlot("key"),
            RespireReadFrom.Primary, CancellationToken.None, discovery: null, observation: default);
        var first = await pool.RentAsync(CancellationToken.None);
        var second = await pool.RentAsync(CancellationToken.None);
        var local = first.AvailabilityZone == "local" ? first : second;
        var remote = ReferenceEquals(local, first) ? second : first;
        pool.Return(local);
        pool.Return(remote); // Ordinary LIFO rental would choose this remote socket.
        using var response = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
        await Assert.That(response.AsString()).IsEqualTo("local");
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("XREAD ")))
            .IsEqualTo(roleFallback ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownPrimaryZonesDoNotRepeatMetadataProbes(bool cluster)
    {
        await using var primary = Node("primary", null, false);
        await using var replica = Node("replica", "remote", true);
        ConfigureTopology(primary, replica);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
#pragma warning restore RESP010
        if (!cluster)
        {
            // Validate both physical replica sockets before asserting steady-state preference.
            await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, default, default);
            await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        }
        for (var index = 0; index < 8; index++)
            await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("replica");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "INFO SERVER")).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, RespireReadFrom.AzAffinity, "local", "replica")]
    [Arguments(true, RespireReadFrom.AzAffinity, "local", "replica")]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, "local", "replica")]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, "local", "replica")]
    [Arguments(false, RespireReadFrom.AzAffinity, "remote", "replica")]
    [Arguments(true, RespireReadFrom.AzAffinity, "remote", "replica")]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, "remote", "primary")]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, "remote", "primary")]
    public async Task BlockingStandaloneReadSelectsEndpoint(bool sentinelMode, RespireReadFrom policy, string replicaZone, string expected)
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", replicaZone, true);
        await using var sentinel = Sentinel(primary, () => [replica]);
        var options = Options(sentinelMode ? sentinel : primary, sentinelMode ? [] : [replica], false, policy)
            with { SentinelPrimaryName = sentinelMode ? "primary" : null };
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(options);
#pragma warning restore RESP010
        for (var index = 0; index < 3; index++)
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
            await Assert.That(reply.AsString()).IsEqualTo(expected);
        }
        var selected = expected == "replica" ? replica : primary;
        var other = expected == "replica" ? primary : replica;
        var readIds = selected.ReceivedCommands.Select((command, index) => (command, index))
            .Where(item => item.command.StartsWith("XREAD "))
            .Select(item => selected.ReceivedConnectionIds[item.index]).Distinct().ToArray();
        await Assert.That(readIds.Length).IsEqualTo(1); // Successful blocking reads reuse their dedicated lease.
        await Assert.That(other.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(replica.ReceivedCommands.Contains("READONLY")).IsFalse();
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    public async Task BlockingStandaloneFallbackSwitchesRoleOnlyOnce(bool primaryFirst, bool bothFail, bool fallbackDeadline)
    {
        await using var primary = Node("primary", primaryFirst ? "local" : "remote", false);
        await using var replica = Node("replica", primaryFirst ? "remote" : "local", true);
        var first = primaryFirst ? primary : replica;
        var second = primaryFirst ? replica : primary;
        var fallbackConnections = 0;
        RejectReads(first);
        if (bothFail) RejectReads(second);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { TestingStreamFactory = fallbackDeadline ? OpenStreamAsync : null });
#pragma warning restore RESP010
        if (bothFail || fallbackDeadline)
        {
            var error = await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"])).Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains("LOADING unavailable");
        }
        else
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
            await Assert.That(reply.AsString()).IsEqualTo(primaryFirst ? "replica" : "primary");
        }
        await Assert.That(first.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        await Assert.That(second.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(fallbackDeadline ? 0 : 1);

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port == second.Port && Interlocked.Increment(ref fallbackConnections) > 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }

        static void RejectReads(FakeRespServer node)
        {
            var previous = node.ReplyOverride!;
            node.ReplyOverride = (id, command) => command.StartsWith("XREAD ")
                ? "-LOADING unavailable\r\n"u8.ToArray() : previous(id, command);
        }
    }

    [Test]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task DedicatedPrimaryHandshakeFailureFallsBackToReplica(RespireReadFrom policy)
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "remote", true);
        var previous = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) =>
        {
            if (id > 0 && command == "HELLO 3") primary.CloseConnection(id);
            return previous(id, command);
        };
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, policy)
            with { Protocol = RespireProtocol.Resp3 });
#pragma warning restore RESP010
        using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"])
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("replica");
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(client.Core.Multiplexer.GetConnection().IsConnected).IsTrue();
    }

    [Test]
    [Arguments(RespireReadFrom.AzAffinity, false, false)]
    [Arguments(RespireReadFrom.AzAffinity, false, true)]
    [Arguments(RespireReadFrom.AzAffinity, true, false)]
    [Arguments(RespireReadFrom.AzAffinity, true, true)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, false, false)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, false, true)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true, false)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true, true)]
    public async Task DedicatedConnectDeadlineFallsBackForEveryEligiblePolicy(RespireReadFrom policy, bool useSentinel, bool cancelCaller)
    {
        await using var primary = Node("primary", "local", false);
        await using var first = Node("first", "remote", true);
        await using var second = Node("second", "remote", true);
        await using var sentinel = Sentinel(primary, () => [first, second]);
        var connections = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var failedPort = 0;
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(
            Options(useSentinel ? sentinel : primary, useSentinel ? [] : [first, second], false, policy) with
            {
                Protocol = RespireProtocol.Resp3, SentinelPrimaryName = useSentinel ? "primary" : null,
                TestingStreamFactory = OpenStreamAsync,
            });
#pragma warning restore RESP010
        using var caller = new CancellationTokenSource();
        var read = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"], cancellationToken: caller.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (cancelCaller)
        {
            await connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var error = await Assert.That(async () => await read).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(primary.ReceivedCommands.Concat(first.ReceivedCommands).Concat(second.ReceivedCommands)
                .Any(command => command.StartsWith("XREAD "))).IsFalse();
            return;
        }
        using var reply = await read;
        await Assert.That(caller.IsCancellationRequested).IsFalse();
        await Assert.That(failedPort).IsNotEqualTo(0);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        var failed = new[] { primary, first, second }.Single(node => node.Port == failedPort);
        await Assert.That(failed.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(first.ReceivedCommands.Concat(second.ReceivedCommands)
            .Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        await Assert.That(reply.AsString()).IsEqualTo(first.ReceivedCommands.Any(command => command.StartsWith("XREAD "))
            ? "first" : "second");

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port != sentinel.Port && connections.AddOrUpdate(port, 1, (_, count) => count + 1) == 2
                && Interlocked.CompareExchange(ref failedPort, port, 0) == 0)
            {
                connecting.TrySetResult();
                // The deadline case keeps the caller live; its control cancels at this boundary.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    [Test]
    [Arguments(RespireReadFrom.AzAffinity, false, false, false)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, false, false, false)]
    [Arguments(RespireReadFrom.AzAffinity, false, true, false)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, false, true, false)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true, false, false)]
    [Arguments(RespireReadFrom.AzAffinity, false, false, true)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true, false, true)]
    [Arguments(RespireReadFrom.PrimaryPreferred, false, false, false)]
    [Arguments(RespireReadFrom.ReplicaPreferred, false, false, false)]
    [Arguments(RespireReadFrom.ReplicaPreferred, false, true, false)]
    [Arguments(RespireReadFrom.Replica, false, false, false)]
    [Arguments(RespireReadFrom.Replica, false, true, false)]
    [Arguments(RespireReadFrom.Primary, false, false, false)]
    [Arguments(RespireReadFrom.Nearest, false, false, false)]
    public async Task ClusterDedicatedDeadlineTriesRemainingCandidates(
        RespireReadFrom policy, bool localPrimary, bool onlyReplicaFails, bool cancelCaller)
    {
        await using var primary = Node("primary", localPrimary ? "local" : "remote", false);
        await using var first = Node("first", "remote", true);
        await using var second = Node("second", "remote", true);
        ConfigureTopology(primary, onlyReplicaFails ? [first] : [first, second]);
        var connections = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var failedPort = 0;
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true, policy) with
        {
            Protocol = RespireProtocol.Resp3, TestingStreamFactory = OpenStreamAsync,
            ClusterTopologyRefreshInterval = null,
        });
#pragma warning restore RESP010
        using var caller = new CancellationTokenSource();
        var read = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"], cancellationToken: caller.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (cancelCaller)
        {
            await connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var error = await Assert.That(async () => await read).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(primary.ReceivedCommands.Concat(first.ReceivedCommands).Concat(second.ReceivedCommands)
                .Any(command => command.StartsWith("XREAD "))).IsFalse();
            return;
        }
        if (policy == RespireReadFrom.Primary || policy == RespireReadFrom.Replica && onlyReplicaFails)
        {
            var error = await Assert.That(async () => await read).Throws<RespireTimeoutException>();
            await Assert.That(error!.CommandName).IsEqualTo("CONNECT");
            await Assert.That(error.Timeout).IsEqualTo(TimeSpan.FromSeconds(2));
            await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
            await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", failedPort));
            await Assert.That(error.InnerException is OperationCanceledException).IsTrue();
            await Assert.That(caller.IsCancellationRequested).IsFalse();
            await Assert.That(primary.ReceivedCommands.Concat(first.ReceivedCommands).Concat(second.ReceivedCommands)
                .Any(command => command.StartsWith("XREAD "))).IsFalse();
            return;
        }
        using var reply = await read;
        await Assert.That(caller.IsCancellationRequested).IsFalse();
        await Assert.That(failedPort).IsNotEqualTo(0);
        var failed = new[] { primary, first, second }.Single(node => node.Port == failedPort);
        await Assert.That(failed.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(primary.ReceivedCommands.Concat(first.ReceivedCommands).Concat(second.ReceivedCommands)
            .Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        if (onlyReplicaFails) await Assert.That(reply.AsString()).IsEqualTo("primary");
        else if (policy != RespireReadFrom.Nearest) await Assert.That(reply.AsString()).IsNotEqualTo("primary");

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (connections.AddOrUpdate(port, 1, (_, count) => count + 1) == 2
                && Interlocked.CompareExchange(ref failedPort, port, 0) == 0)
            {
                connecting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterDedicatedFallbackDeadlinePreservesOriginalRejection(bool startsOnReplica)
    {
        await using var primary = Node("primary", startsOnReplica ? "remote" : "local", false);
        await using var replica = Node("replica", startsOnReplica ? "local" : "remote", true);
        ConfigureTopology(primary, replica);
        var rejecting = startsOnReplica ? replica : primary;
        var target = startsOnReplica ? primary : replica;
        var original = rejecting.ReplyOverride!;
        rejecting.ReplyOverride = (id, command) => command.StartsWith("XREAD ")
            ? "-LOADING original rejection\r\n"u8.ToArray() : original(id, command);
        var targetConnections = 0;
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with
        {
            Protocol = RespireProtocol.Resp3, TestingStreamFactory = OpenStreamAsync,
            ClusterTopologyRefreshInterval = null,
        });
#pragma warning restore RESP010
        var error = await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask()).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("LOADING");
        await Assert.That(error.Message).Contains("original rejection");
        await Assert.That(rejecting.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port == target.Port && Interlocked.Increment(ref targetConnections) == 2)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    [Test]
    public async Task ClusterDedicatedFailureTriesMoreCandidatesThanRedirectLimit()
    {
        await using var primary = Node("primary", "remote", false);
        var replicas = Enumerable.Range(0, 7).Select(index => Node($"replica-{index}", "local", true)).ToArray();
        try
        {
            ConfigureTopology(primary, replicas);
            var connections = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
            var rentals = 0;
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
            await using var client = await RespireClient.ConnectAsync(Options(primary, [], true, RespireReadFrom.Replica) with
            {
                Protocol = RespireProtocol.Resp3, TestingStreamFactory = OpenStreamAsync,
                ClusterTopologyRefreshInterval = null,
            });
#pragma warning restore RESP010
            using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
            await Assert.That(rentals).IsEqualTo(7);
            await Assert.That(reply.AsString()!.StartsWith("replica-")).IsTrue();
            await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
            await Assert.That(replicas.SelectMany(node => node.ReceivedCommands)
                .Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);

            async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
            {
                if (port != primary.Port && connections.AddOrUpdate(port, 1, (_, count) => count + 1) == 2
                    && Interlocked.Increment(ref rentals) <= 6)
                    throw new IOException("Dedicated candidate unavailable.");
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        }
        finally { foreach (var replica in replicas) await replica.DisposeAsync(); }
    }

    [Test]
    public async Task ClusterDedicatedAskDeadlineDoesNotReselectSlotReplicas()
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "local", true);
        await using var importing = Node("importing", "local", false);
        ConfigureTopology(primary, replica);
        var original = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command.StartsWith("XREAD ")
            ? Encoding.ASCII.GetBytes($"-ASK {ClusterHash.GetSlot("key")} 127.0.0.1:{importing.Port}\r\n")
            : original(id, command);
        var importingConnections = 0;
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true, RespireReadFrom.AzAffinity) with
        {
            Protocol = RespireProtocol.Resp3, TestingStreamFactory = OpenStreamAsync,
            ClusterTopologyRefreshInterval = null,
        });
#pragma warning restore RESP010
        var error = await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask()).Throws<RespireTimeoutException>();
        await Assert.That(error!.CommandName).IsEqualTo("CONNECT");
        await Assert.That(error.Timeout).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", importing.Port));
        await Assert.That(error.InnerException is OperationCanceledException).IsTrue();
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Concat(importing.ReceivedCommands)
            .Any(command => command.StartsWith("XREAD "))).IsFalse();

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port == importing.Port && Interlocked.Increment(ref importingConnections) == 2)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    [Test]
    [Arguments(RespireReadFrom.AzAffinity)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task FailedDedicatedPrimaryWithNoSentinelReplicasPreservesConnectionError(RespireReadFrom policy)
    {
        await using var primary = Node("primary", "local", false);
        await using var sentinel = Sentinel(primary, () => []);
        var previous = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) =>
        {
            if (id > 0 && command == "HELLO 3") primary.CloseConnection(id);
            return previous(id, command);
        };
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], false, policy)
            with { Protocol = RespireProtocol.Resp3, SentinelPrimaryName = "primary" });
#pragma warning restore RESP010
        var error = await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
        await Assert.That(error!.Message.Contains("No eligible read replicas", StringComparison.Ordinal)).IsFalse();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "HELLO 3")).IsGreaterThan(1);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    // Shared routing matrix: both policies x ASK/MOVED x direct/role-fallback x ordinary/batch/stream.
    // Each row must keep the local socket and send once per visited endpoint. The reverse role
    // direction is covered by ReplicaRoleFallbackRetainsZoneAfterMovedRefresh: a narrowed replica
    // read never returns to the primary. PinnedCursorKeepsLocalPhysicalSocket covers both policies
    // and shared/per-enumeration pins without changing the server that owns the cursor.
    [Test]
    [Arguments("ASK", 0, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 0, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 1, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 1, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 2, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 2, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 0, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 0, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 1, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 1, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 2, false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("MOVED", 2, true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments("ASK", 0, false, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 0, true, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 1, false, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 1, true, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 2, false, RespireReadFrom.AzAffinity)]
    [Arguments("ASK", 2, true, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 0, false, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 0, true, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 1, false, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 1, true, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 2, false, RespireReadFrom.AzAffinity)]
    [Arguments("MOVED", 2, true, RespireReadFrom.AzAffinity)]
    public async Task ClusterRedirectKeepsLocalPhysicalSocket(
        string redirect, int mode, bool afterRoleFallback, RespireReadFrom policy)
    {
        await using var primary = Node("primary", "local", false);
        await using var target = Node("target", "remote", false);
        await using var replica = Node("replica", "local", true);
        ConfigureTopology(primary, afterRoleFallback ? [replica] : []);
        var replicaReply = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command.StartsWith("GET ")
            ? "-LOADING replica unavailable\r\n"u8.ToArray() : replicaReply(id, command);
        var key = Enumerable.Range(0, 100).Select(index => $"zone-key-{index}")
            .First(value => ClusterHash.GetSlot(value) % 2 == 1);
        var slot = ClusterHash.GetSlot(key);
        var primaryReply = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command.StartsWith("GET ")
            ? Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n") : primaryReply(id, command);
        var targetReply = target.ReplyOverride!;
        target.ReplyOverride = (id, command) => command switch
        {
            "INFO SERVER" => Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n"),
            "CLUSTER SLOTS" => "-ERR topology unavailable\r\n"u8.ToArray(),
            _ when command.StartsWith("GET ") => Bulk(id % 2 == 0 ? "local-socket" : "remote-socket"),
            _ => targetReply(id, command),
        };
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true,
            policy) with { Connections = 2, ClusterTopologyRefreshInterval = null });
#pragma warning restore RESP010
        if (mode == 1)
        {
            using var batch = client.CreateBatch();
            var result = batch.Strings.GetString(key);
            await batch.ExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await result).IsEqualTo("local-socket");
        }
        else if (mode == 2)
        {
            await using var stream = await client.Strings.GetStreamAsync(key);
            using var reader = new StreamReader(stream!);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("local-socket");
        }
        else await Assert.That(await client.GetStringAsync(key)).IsEqualTo("local-socket");
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("GET ")))
            .IsEqualTo(afterRoleFallback ? 1 : 0);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    public async Task DedicatedReplicaReselectionKeepsZoneWithoutReturningPrimary()
    {
        await using var primary = Node("primary", "local", false);
        await using var localReplica = Node("local", "local", true);
        await using var remoteReplica = Node("remote", "remote", true);
        ConfigureTopology(primary, localReplica, remoteReplica);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { ClusterTopologyRefreshInterval = null });
#pragma warning restore RESP010
        var router = client.Core.Cluster!;
        var slot = ClusterHash.GetSlot("key");
        var previous = await router.GetReadDedicatedPoolAsync(slot, RespireReadFrom.Replica, default, null, observation: default);
        await previous.RetireAsync();
        var lease = await router.RentDedicatedConnectionAsync(previous,
            new ClusterRouter.DedicatedRoute(slot, RespireReadFrom.Replica), default, null, preferredZone: "local", observation: default);
        try
        {
            await Assert.That(lease.Connection.Port).IsEqualTo(localReplica.Port);
            await Assert.That(lease.Pool.IsReadOnly).IsTrue();
        }
        finally { lease.Pool.Return(lease.Connection); }
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    public async Task ReplicaRoleFallbackRetainsZoneAfterMovedRefresh(int mode, bool mixedSockets)
        => await RunReplicaRoleFallbackAsync(mode, mixedSockets);

    [Test]
    public async Task ReplicaRoleFallbackFailureReportsProtocolAndRuntimeEvidence()
    {
        // Cancel at a protocol milestone, not after a delay: this exercises the real batch
        // failure report without depending on the intermittent timeout in #1749.
        var error = await Assert.That(async () => await RunReplicaRoleFallbackAsync(1, false, cancelRefresh: true))
            .Throws<InvalidOperationException>();
        await Assert.That(error!.InnerException is OperationCanceledException).IsTrue();
        await Assert.That(error.Message).Contains("read started");
        await Assert.That(error.Message).Contains("refresh reply withheld; cancel requested");
        await Assert.That(error.Message).Contains("canceled=True");
        await Assert.That(error.Message).Contains("primary: reads=1");
        await Assert.That(error.Message).Contains("old-replica: reads=1");
        await Assert.That(error.Message).Contains("target: reads=0");
        await Assert.That(error.Message).Contains("local: reads=0");
        await Assert.That(error.Message).Contains("remote: reads=0");
        await Assert.That(error.Message).Contains("CLUSTER SLOTS");
        await Assert.That(error.Message).Contains("workers=");
        await Assert.That(error.Message).Contains("pending=");
        await Assert.That(error.Message).Contains("completed-delta=");
        await Assert.That(error.Message).Contains("gc-delta=");
        await Assert.That(error.Message).Contains("gc-pause-delta-ms=");
    }

    private static async Task RunReplicaRoleFallbackAsync(int mode, bool mixedSockets, bool cancelRefresh = false)
    {
        await using var primary = Node("primary", "local", false);
        await using var oldReplica = Node("old-replica", "remote", true);
        await using var target = Node("target", "remote", false);
        await using var localReplica = Node("local", "local", true);
        await using var remoteReplica = Node("remote", "remote", true);
        ConfigureTopology(primary, oldReplica);
        // A new set starts round-robin at index 1: without affinity it picks the remote endpoint.
        ConfigureTopology(target, mixedSockets ? [localReplica] : [localReplica, remoteReplica]);
        var key = Enumerable.Range(0, 100).Select(index => $"role-redirect-{index}")
            .First(value => ClusterHash.GetSlot(value) % 2 == 1);
        var slot = ClusterHash.GetSlot(key);
        if (mixedSockets)
        {
            var previous = localReplica.ReplyOverride!;
            localReplica.ReplyOverride = (id, command) => command switch
            {
                "INFO SERVER" => Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n"),
                _ when command.StartsWith("GET ") => Bulk(id % 2 == 0 ? "local" : "remote"),
                _ => previous(id, command),
            };
        }
        var redirected = 0;
        var originalPrimaryReply = primary.ReplyOverride!;
        // Keep forwarding the scripted topology, not the target's diagnostic wrapper:
        // a query received by primary must not appear as target socket traffic.
        var refreshedTopologyReply = target.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command switch
        {
            _ when command.StartsWith("GET ") || command.StartsWith("XREAD ") => "-LOADING primary unavailable\r\n"u8.ToArray(),
            "CLUSTER SLOTS" when Volatile.Read(ref redirected) != 0 => refreshedTopologyReply(id, command),
            _ => originalPrimaryReply(id, command),
        };
        var originalReplicaReply = oldReplica.ReplyOverride!;
        oldReplica.ReplyOverride = (id, command) =>
        {
            if (!command.StartsWith("GET ") && !command.StartsWith("XREAD ")) return originalReplicaReply(id, command);
            Volatile.Write(ref redirected, 1);
            return Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n");
        };
        var trace = new ReplicaFallbackTrace(
            ("primary", primary), ("old-replica", oldReplica), ("target", target),
            ("local", localReplica), ("remote", remoteReplica));
        using var timeout = new CancellationTokenSource();
        if (cancelRefresh)
        {
            var previous = target.ReplyOverride!;
            target.ReplyOverride = (id, command) =>
            {
                if (command != "CLUSTER SLOTS") return previous(id, command);
                trace.Record($"target[{id}] refresh reply withheld; cancel requested");
                timeout.Cancel();
                return [];
            };
        }
        trace.ObserveReplies();
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with
        {
            Connections = mixedSockets ? 2 : 1, ClusterTopologyRefreshInterval = null,
        });
#pragma warning restore RESP010
        trace.Record("read started");
        using var cancellation = timeout.Token.Register(() => trace.Record("read token canceled"));
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            string? result;
            if (mode == 1)
            {
                using var batch = client.CreateBatch();
                var pending = batch.Strings.GetString(key);
                await batch.ExecuteAsync(timeout.Token);
                result = await pending;
            }
            else if (mode == 2)
            {
                await using var stream = await client.Strings.GetStreamAsync(key, timeout.Token);
                using var reader = new StreamReader(stream!);
                result = await reader.ReadToEndAsync(timeout.Token);
            }
            else if (mode == 3)
            {
                using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                    ["BLOCK", 1, "STREAMS", key, "0"], cancellationToken: timeout.Token);
                result = reply.AsString();
            }
            else result = await client.GetStringAsync(key, timeout.Token);
            await Assert.That(result).IsEqualTo("local");
            await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("GET ") || command.StartsWith("XREAD "))).IsEqualTo(1);
            await Assert.That(oldReplica.ReceivedCommands.Count(command => command.StartsWith("GET ") || command.StartsWith("XREAD "))).IsEqualTo(1);
        }
        catch (Exception error)
        {
            // Snapshot before client/server disposal adds unrelated time and traffic.
            trace.Record($"read failed; canceled={timeout.IsCancellationRequested}");
            throw new InvalidOperationException(
                $"Cluster replica fallback failed (mode={mode}, mixedSockets={mixedSockets}).{Environment.NewLine}{trace.Describe()}", error);
        }
    }

    private sealed class ReplicaFallbackTrace(params (string Name, FakeRespServer Server)[] nodes)
    {
        private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
        private readonly long _completed = ThreadPool.CompletedWorkItemCount;
        private readonly int[] _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        private readonly TimeSpan _pause = GC.GetTotalPauseDuration();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Entry> _events = new();

        public void ObserveReplies()
        {
            Record("fixture ready");
            foreach (var (name, server) in nodes)
            {
                var previous = server.ReplyOverride!;
                server.ReplyOverride = (id, command) =>
                {
                    Record($"{name}[{id}] port={server.Port} received {command}");
                    var reply = previous(id, command);
                    Record($"{name}[{id}] reply prepared; bytes={reply?.Length ?? 0}");
                    return reply;
                };
            }
        }

        public void Record(string phase)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            // Defer counter formatting until failure to minimize diagnostic work on the wire path.
            _events.Enqueue(new(elapsed, phase, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                ThreadPool.CompletedWorkItemCount - _completed, GC.CollectionCount(0) - _collections[0],
                GC.CollectionCount(1) - _collections[1], GC.CollectionCount(2) - _collections[2],
                (GC.GetTotalPauseDuration() - _pause).TotalMilliseconds));
        }

        public string Describe()
        {
            var text = new StringBuilder("Protocol timestamps precede socket writes; a prepared reply does not prove client receipt. Runtime counters are process-wide, not proof of causation.");
            foreach (var entry in _events.OrderBy(entry => entry.ElapsedMilliseconds))
                text.AppendLine().Append(FormattableString.Invariant(
                    $"{entry.ElapsedMilliseconds:F3} ms: {entry.Phase}; workers={entry.Workers}, pending={entry.Pending}, completed-delta={entry.Completed}, gc-delta={entry.Gen0}/{entry.Gen1}/{entry.Gen2}, gc-pause-delta-ms={entry.PauseMilliseconds:F3}"));
            foreach (var (name, server) in nodes)
                text.AppendLine().Append(name).Append(": reads=").Append(server.ReceivedCommands.Count(
                    command => command.StartsWith("GET ") || command.StartsWith("XREAD ")));
            return text.ToString();
        }

        private readonly record struct Entry(double ElapsedMilliseconds, string Phase, int Workers,
            long Pending, long Completed, int Gen0, int Gen1, int Gen2, double PauseMilliseconds);
    }

    [Test]
    [Arguments(true, 0)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    [Arguments(false, 0)]
    [Arguments(false, 1)]
    [Arguments(false, 2)]
    public async Task ClusterRetirementMaintainsZoneAfterAskOrRoleFallback(bool asking, int mode)
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "local", true);
        await using var importing = Node("importing", "local", false);
        ConfigureTopology(primary, asking ? [] : [replica]);
        var target = importing;
        var key = Enumerable.Range(0, 100).Select(index => $"retirement-zone-{index}")
            .First(value => ClusterHash.GetSlot(value) % 2 == 1);
        var slot = ClusterHash.GetSlot(key);
        var replicaReply = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => command.StartsWith("GET ")
            ? "-LOADING replica unavailable\r\n"u8.ToArray() : replicaReply(id, command);
        if (asking)
        {
            var primaryReply = primary.ReplyOverride!;
            primary.ReplyOverride = (id, command) => command.StartsWith("GET ")
                ? Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n") : primaryReply(id, command);
        }
        var targetReply = target.ReplyOverride!;
        target.ReplyOverride = (id, command) => command switch
        {
            "INFO SERVER" => Bulk($"availability_zone:{(id % 2 == 1 ? "remote" : "local")}\r\n"),
            _ when command.StartsWith("GET ") => Bulk(id % 2 == 1 ? "remote-socket" : "local-socket"),
            _ => targetReply(id, command),
        };
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2, ClusterTopologyRefreshInterval = null });
#pragma warning restore RESP010
        var router = client.Core.Cluster!;
        var source = router.GetKnownSlotOwner(slot)!.GetConnection(slot);
        var selected = await router.GetRedirectConnectionAsync(
            new RespireServerException($"ASK {slot} 127.0.0.1:{target.Port}"),
            source, CancellationToken.None, slot, null, default, "local");
        await Assert.That(selected.AvailabilityZone).IsEqualTo("local");
        await Assert.That(selected.Multiplexer!.GetConnection(slot).AvailabilityZone).IsEqualTo("remote");
        var retired = 0;
        Task retirement = Task.CompletedTask;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == "Respire",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "GET" && activity.GetTagItem("server.port") is int port
                    && port == (asking ? target.Port : primary.Port)
                    && (asking ? primary : replica).ReceivedCommands.Any(command => command.StartsWith("GET "))
                    && Interlocked.CompareExchange(ref retired, 1, 0) == 0)
                {
                    // A replica refresh can discard the prewarmed redirect node. Retire the
                    // generation actually selected now, immediately before application admission.
                    var current = asking ? router.GetOrCreateNode(new("127.0.0.1", target.Port), observe: false, redirect: true)
                        : router.GetKnownSlotOwner(slot)!;
                    router.ApplyTopology([new(0, 16383, new("127.0.0.1", asking ? primary.Port : target.Port),
                        asking ? "primary" : "promoted", [])], router.TopologyVersion, long.MaxValue);
                    retirement = current.RetireAsync();
                }
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        if (mode == 1)
        {
            using var batch = client.CreateBatch();
            var result = batch.Strings.GetString(key);
            await batch.ExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await result).IsEqualTo("local-socket");
        }
        else if (mode == 2)
        {
            await using var stream = await client.Strings.GetStreamAsync(key);
            using var reader = new StreamReader(stream!);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("local-socket");
        }
        else await Assert.That(await client.GetStringAsync(key)).IsEqualTo("local-socket");
        await Assert.That(retired).IsEqualTo(1);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
        if (asking) await Assert.That(router.GetKnownSlotOwner(slot)!.Port).IsEqualTo(primary.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplicaFallbackRetirementKeepsZoneAndNeverReturnsPrimary(bool replacementAvailable)
    {
        await using var primary = Node("primary", "local", false);
        await using var previous = Node("previous", "local", true);
        await using var replacement = Node("replacement", "remote", true);
        ConfigureTopology(primary, previous);
        var original = replacement.ReplyOverride!;
        replacement.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n") : original(id, command);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2, ClusterTopologyRefreshInterval = null });
#pragma warning restore RESP010
        var router = client.Core.Cluster!;
        var fallback = new ReadFallbackPolicy.RoleFallback(RespireReadFrom.AzAffinityReplicasAndPrimary);
        await Assert.That(fallback.TrySwitch(new RespireServerException("LOADING unavailable"), onReplica: false)).IsTrue();
        var old = await router.GetOtherRoleReadConnectionAsync(1, fallback, CancellationToken.None, null, observation: default);
        router.ApplyTopology([new(0, 16383, new("127.0.0.1", primary.Port), "primary", [])
        {
            Replicas = replacementAvailable ? [new(new("127.0.0.1", replacement.Port), "replacement", [])] : [],
        }], router.TopologyVersion, long.MaxValue);
        await Assert.That(old.Multiplexer!.IsRetired).IsTrue();
        if (replacementAvailable)
        {
            var current = await router.GetReadReplacementConnectionAsync(1, RespireReadFrom.Replica,
                CancellationToken.None, null, default, "local");
            await Assert.That(current.Port).IsEqualTo(replacement.Port);
            await Assert.That(current.AvailabilityZone).IsEqualTo("local");
            await Assert.That(current.Multiplexer!.Options.ReadOnly).IsTrue();
        }
        else
            await Assert.That(async () => await router.GetReadReplacementConnectionAsync(1, RespireReadFrom.Replica,
                CancellationToken.None, null, default, "local")).Throws<RespireConnectionException>();
    }

    [Test, NotInParallel]
    public async Task WarmBlockingReplicaRentalAllocatesNothing()
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "local", true);
#pragma warning disable RESP010 // This zone-routing scenario owns and disposes its client to isolate topology and physical socket metadata.
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, RespireReadFrom.AzAffinity));
#pragma warning restore RESP010
        var router = client.Core.ReadRouter;
        router.RoleRevalidationInterval = TimeSpan.FromDays(1);
        var first = await router.RentDedicatedConnectionAsync(RespireReadFrom.AzAffinity, CancellationToken.None, default, "local");
        first.Pool.Return(first.Connection);
        for (var index = 0; index < 20; index++) { MeasureDedicatedRental(router, false); MeasureDedicatedRental(router, true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Actual: MeasureDedicatedRental(router, false), Control: MeasureDedicatedRental(router, true)));
        await Assert.That(measured.Actual).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDedicatedRental(ReadEndpointRouter router, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            var pending = router.RentDedicatedConnectionAsync(RespireReadFrom.AzAffinity, CancellationToken.None, default, "local");
            if (!pending.IsCompletedSuccessfully) throw new InvalidOperationException("Warm rental performed asynchronous work.");
#pragma warning disable RESP009 // IsCompletedSuccessfully above guarantees this allocation probe cannot block or leave the measuring thread.
            var lease = pending.Result;
#pragma warning restore RESP009
            lease.Pool.Return(lease.Connection);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static async Task<string> ObservedReadZoneAsync(FakeRespServer server)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var index = server.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("GET "));
            if (index >= 0) return server.ReceivedConnectionIds[index] % 2 == 0 ? "local" : "remote";
            await Task.Delay(10, limit.Token);
        }
    }

    private static void ConfigureMixedSocketZones(FakeRespServer server)
    {
        var original = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command switch
        {
            "INFO SERVER" => Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n"),
            _ when command.StartsWith("GET ") => Bulk(id % 2 == 0 ? "local" : "remote"),
            _ => original(id, command),
        };
    }

    private static FakeRespServer Sentinel(FakeRespServer primary, Func<FakeRespServer[]> replicas) => new(16)
    {
        ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
            ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${primary.Port.ToString().Length}\r\n{primary.Port}\r\n")
            : command.StartsWith("SENTINEL REPLICAS ")
                ? Encoding.ASCII.GetBytes(ReplicaReply(replicas())) : "*0\r\n"u8.ToArray(),
    };

    private static string ReplicaReply(FakeRespServer[] replicas)
        => $"*{replicas.Length}\r\n" + string.Concat(replicas.Select(replica =>
            $"*6\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{replica.Port}\r\n+flags\r\n+slave\r\n"));

    private static RespireOptions Options(FakeRespServer primary, FakeRespServer[] replicas, bool cluster, RespireReadFrom policy)
        => new()
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = cluster ? [] : replicas.Select(node => new RespireEndpoint("127.0.0.1", node.Port)).ToArray(),
            UseCluster = cluster, Protocol = RespireProtocol.Resp2, Connections = 1,
            ClientAvailabilityZone = "local", ReadFrom = policy,
            ConnectTimeout = TimeSpan.FromSeconds(2), CommandTimeout = TimeSpan.FromSeconds(5),
        };

    private static FakeRespServer Node(string name, string? zone, bool replica)
        => new(8)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Encoding.ASCII.GetBytes(zone is null ? "%1\r\n+proto\r\n:3\r\n"
                    : $"%2\r\n+proto\r\n:3\r\n+availability_zone\r\n+{zone}\r\n"),
                "INFO SERVER" => Bulk(zone is null ? "# Server\r\n" : $"availability_zone:{zone}\r\n"),
                "ROLE" => replica ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray()
                    : "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray(),
                _ when command.StartsWith("GET ") || command.StartsWith("XREAD ") => Bulk(name),
                _ => FakeRespServer.OkReply,
            },
        };

    private static void ConfigureTopology(FakeRespServer primary, params FakeRespServer[] replicas)
    {
        var nodes = new[] { primary }.Concat(replicas).ToArray();
        var topology = new StringBuilder($"*1\r\n*{2 + nodes.Length}\r\n:0\r\n:16383\r\n");
        foreach (var node in nodes)
            topology.Append($"*3\r\n$9\r\n127.0.0.1\r\n:{node.Port}\r\n${node.Port.ToString().Length}\r\n{node.Port}\r\n");
        var reply = Encoding.ASCII.GetBytes(topology.ToString());
        var previous = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command == "CLUSTER SLOTS" ? reply : previous(id, command);
    }

    private static byte[] Bulk(string value) => Encoding.ASCII.GetBytes($"${value.Length}\r\n{value}\r\n");
}
