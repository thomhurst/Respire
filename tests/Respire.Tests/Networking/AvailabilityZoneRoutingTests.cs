using System.Text;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class AvailabilityZoneRoutingTests
{
    [Test]
    [NotInParallel]
    [Arguments(true)]
    [Arguments(false)]
    public async Task WarmZoneSelectionAndCounterAllocateNothing(bool local)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", local ? "local" : "remote", true);
        await using var second = Node("second", local ? "local" : "remote", true);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica, second], false, RespireReadFrom.AzAffinity)
            with { ReplicaRefreshInterval = TimeSpan.FromMinutes(5) });
        // Warm both round-robin endpoints before asserting synchronous, allocation-free selection.
        await client.GetStringAsync("key");
        await client.GetStringAsync("key");
        var router = client.Core.ReadRouter!;
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
            var selected = router.SelectAsync(RespireReadFrom.AzAffinity, CancellationToken.None);
            if (!selected.IsCompletedSuccessfully) throw new InvalidOperationException("Warm selection must complete synchronously.");
            _ = selected.Result;
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, RespireReadFrom.AzAffinity));
        var old = (await client.Core.ReadRouter!.SelectAsync(RespireReadFrom.AzAffinity, CancellationToken.None)).Connection;
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
        var current = (await client.Core.ReadRouter.SelectAsync(RespireReadFrom.Replica, timeout.Token)).Connection;
        await Assert.That(current.AvailabilityZone).IsEqualTo("changed");
    }

    [Test]
    [Arguments(false, RespireReadFrom.AzAffinity)]
    [Arguments(true, RespireReadFrom.AzAffinity)]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task SameZoneReplicaWinsAndWritesStayPrimary(bool cluster, RespireReadFrom policy)
    {
        await using var primary = Node("primary", "local", false);
        await using var local = Node("local", "local", true);
        await using var remote = Node("remote", "remote", true);
        ConfigureTopology(primary, local, remote);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [local, remote], cluster, policy));
        for (var index = 0; index < 8; index++)
            await Assert.That(await client.GetStringAsync("{zone}:key")).IsEqualTo("local");
        using (var reply = await client.ExecuteAsync(RespireCommands.String.GET, "{zone}:key"))
            await Assert.That(reply.AsString()).IsEqualTo("local");
        await Assert.That(await client.SetAsync("{zone}:key", "value")).IsTrue();
        await Assert.That(primary.ReceivedCommands.Contains("SET {zone}:key value")).IsTrue();
        await Assert.That(remote.ReceivedCommands.Any(command => command.StartsWith("GET "))).IsFalse();
    }

    [Test]
    [Arguments(false, RespireReadFrom.AzAffinity, "remote")]
    [Arguments(true, RespireReadFrom.AzAffinity, "remote")]
    [Arguments(false, RespireReadFrom.AzAffinityReplicasAndPrimary, "primary")]
    [Arguments(true, RespireReadFrom.AzAffinityReplicasAndPrimary, "primary")]
    public async Task LocalPrimaryPrecedesRemoteReplicaOnlyWhenRequested(bool cluster, RespireReadFrom policy, string expected)
    {
        await using var primary = Node("primary", "local", false);
        await using var remote = Node("remote", "remote", true);
        ConfigureTopology(primary, remote);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [remote], cluster, policy));
        await Assert.That(await client.GetStringAsync("{zone}:key")).IsEqualTo(expected);
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [first, second, third], false,
            RespireReadFrom.AzAffinity));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var router = client.Core.ReadRouter;
        var old = await router.GetReplicaFromEndpointsAsync([new("127.0.0.1", first.Port)], timeout.Token,
            RespireReadFrom.AzAffinity);
        // The warm lookup increments rotation once; start the next lookup at first in either array.
        RespireEndpoint[] endpoints = laterProbeFails
            ? [new("127.0.0.1", second.Port), new("127.0.0.1", third.Port), new("127.0.0.1", first.Port)]
            : [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)];
        var selection = router.GetReplicaFromEndpointsAsync(endpoints, timeout.Token, RespireReadFrom.AzAffinity).AsTask();
        await reached.Task.WaitAsync(timeout.Token);
        first.CloseConnections();
        while (old.Connection.IsAcceptingCommands) await Task.Delay(5, timeout.Token);
        await blocked.SendRawAsync(laterProbeFails ? "-LOADING unavailable\r\n"u8.ToArray() : disconnected,
            blocked.ReceivedConnectionIds[^1]);
        var selected = await selection.WaitAsync(timeout.Token);
        await Assert.That(selected.Connection.Port).IsEqualTo(second.Port);
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    [Arguments(RespProtocol.Auto)]
    public async Task PhysicalConnectionCapturesServerZone(RespProtocol protocol)
    {
        await using var server = Node("primary", "zone-a", false);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = protocol, DiscoverAvailabilityZone = true });
        await Assert.That(connection.AvailabilityZone).IsEqualTo("zone-a");
        await Assert.That(server.ReceivedCommands.Contains("INFO SERVER")).IsEqualTo(protocol == RespProtocol.Resp2);
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster, RespireReadFrom.AzAffinity));
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster, RespireReadFrom.AzAffinityReplicasAndPrimary));
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true, RespireReadFrom.AzAffinityReplicasAndPrimary));
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
        var router = client.Core.Cluster!;
        await router.EnsureConnectedAsync(CancellationToken.None, discovery: null);
        for (var slot = 0; slot < 8; slot++)
        {
            var selected = await router.GetOtherRoleReadConnectionAsync(slot,
                RespireReadFrom.AzAffinityReplicasAndPrimary, onReplica: false,
                new RespireServerException("LOADING unavailable"), CancellationToken.None, discovery: null);
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, policy)
            with { ClientAvailabilityZone = zone });
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
    public async Task ZoneLookupAclErrorDoesNotPreventResp2Connection()
    {
        await using var server = Node("primary", null, false);
        var previous = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? "-NOPERM INFO denied\r\n"u8.ToArray() : previous(id, command);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2, DiscoverAvailabilityZone = true, Database = 1 });
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
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 6379)], ReplicaEndpoints = [new("127.0.0.1", 6380)],
        });
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
        await using var client = await RespireClient.ConnectAsync(Options(primary,
            includeRemote ? [local, remote] : [local], false, policy));
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true, policy));
        await Assert.That(async () => await client.GetStringAsync("{zone}:key")).Throws<RespireServerException>();
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    public async Task PrimaryZoneProbeUsesPhysicalConnectionMetadata()
    {
        await using var primary = Node("primary", "remote", false);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], false, RespireReadFrom.Primary));
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
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], false,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2, SentinelPrimaryName = "primary" });
        for (var index = 0; index < 8; index++)
            await Assert.That(await client.GetStringAsync("key")).IsEqualTo("local");
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task PinnedCursorKeepsLocalPhysicalSocket(bool cluster, bool shared, bool primaryPin)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "remote", true);
        ConfigureTopology(primary, replica);
        var mixed = primaryPin ? primary : replica;
        var original = mixed.ReplyOverride!;
        mixed.ReplyOverride = (id, command) => command == "INFO SERVER"
            ? Bulk($"availability_zone:{(id % 2 == 0 ? "local" : "remote")}\r\n") : original(id, command);
        const RespireReadFrom policy = RespireReadFrom.AzAffinityReplicasAndPrimary;
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster, policy)
            with { Connections = 2 });
        ReadAffinity? pin = shared ? null : new();
        var cursors = client.Core.ReadRouter.Cursors;
        for (var page = 0; page < 8; page++)
        {
            var selected = cluster
                ? await cursors.GetClusterConnectionAsync(client.Core.Cluster!, 1, policy, pin, page > 0, CancellationToken.None)
                : await cursors.GetConnectionAsync(client.Core.ReadRouter, policy, pin, page > 0, CancellationToken.None);
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
        var pool = await client.Core.Cluster!.GetReadDedicatedPoolAsync(ClusterHash.GetSlot("key"),
            RespireReadFrom.Primary, CancellationToken.None, discovery: null);
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], cluster,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2 });
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
    [Arguments(false, RespireReadFrom.Replica, "local", "replica")]
    [Arguments(false, RespireReadFrom.ReplicaPreferred, "local", "replica")]
    public async Task BlockingStandaloneReadSelectsEndpoint(bool sentinelMode, RespireReadFrom policy, string replicaZone, string expected)
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", replicaZone, true);
        await using var sentinel = Sentinel(primary, () => [replica]);
        var options = Options(sentinelMode ? sentinel : primary, sentinelMode ? [] : [replica], false, policy)
            with { SentinelPrimaryName = sentinelMode ? "primary" : null };
        await using var client = await RespireClient.ConnectAsync(options);
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
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BlockingStandaloneFallbackSwitchesRoleOnlyOnce(bool primaryFirst, bool bothFail)
    {
        await using var primary = Node("primary", primaryFirst ? "local" : "remote", false);
        await using var replica = Node("replica", primaryFirst ? "remote" : "local", true);
        var first = primaryFirst ? primary : replica;
        var second = primaryFirst ? replica : primary;
        RejectReads(first);
        if (bothFail) RejectReads(second);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false,
            RespireReadFrom.AzAffinityReplicasAndPrimary));
        if (bothFail)
            await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"])).Throws<RespireServerException>();
        else
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
            await Assert.That(reply.AsString()).IsEqualTo(primaryFirst ? "replica" : "primary");
        }
        await Assert.That(first.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        await Assert.That(second.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);

        static void RejectReads(FakeRespServer node)
        {
            var previous = node.ReplyOverride!;
            node.ReplyOverride = (id, command) => command.StartsWith("XREAD ")
                ? "-LOADING unavailable\r\n"u8.ToArray() : previous(id, command);
        }
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred)]
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, policy)
            with { Protocol = RespProtocol.Resp3 });
        using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"])
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("replica");
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(client.Core.Multiplexer.GetConnection().IsConnected).IsTrue();
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred)]
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
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], false, policy)
            with { Protocol = RespProtocol.Resp3, SentinelPrimaryName = "primary" });
        var error = await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
        await Assert.That(error!.Message.Contains("No eligible read replicas", StringComparison.Ordinal)).IsFalse();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "HELLO 3")).IsGreaterThan(1);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    [Test]
    [Arguments("ASK", 0, false)]
    [Arguments("ASK", 0, true)]
    [Arguments("ASK", 1, false)]
    [Arguments("ASK", 1, true)]
    [Arguments("ASK", 2, false)]
    [Arguments("ASK", 2, true)]
    [Arguments("MOVED", 0, false)]
    [Arguments("MOVED", 0, true)]
    [Arguments("MOVED", 1, false)]
    [Arguments("MOVED", 1, true)]
    [Arguments("MOVED", 2, false)]
    [Arguments("MOVED", 2, true)]
    public async Task ClusterRedirectKeepsLocalPhysicalSocket(string redirect, int mode, bool afterRoleFallback)
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
        await using var client = await RespireClient.ConnectAsync(Options(primary, [], true,
            RespireReadFrom.AzAffinityReplicasAndPrimary) with { Connections = 2, ClusterTopologyRefreshInterval = null });
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
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingReplicaValidatesDedicatedSocketRole(bool strict)
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "local", true);
        var previous = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => id > 0 && command == "ROLE"
            ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : previous(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false,
            strict ? RespireReadFrom.Replica : RespireReadFrom.AzAffinity));
        if (strict)
            await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"])).Throws<RespireConnectionException>();
        else
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
            await Assert.That(reply.AsString()).IsEqualTo("primary");
        }
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    [Test]
    public async Task BlockingReplicaKeepsMultiplexedReadsFreeAndHonorsCallerCancellation()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "local", true);
        replica.SuppressReply = command =>
        {
            if (!command.StartsWith("XREAD ")) return false;
            arrived.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false,
            RespireReadFrom.AzAffinity) with { CommandTimeout = TimeSpan.FromMilliseconds(500) });
        using var caller = new CancellationTokenSource();
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 0, "STREAMS", "key", "0"], cancellationToken: caller.Token).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(750);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("replica");
        caller.Cancel();
        var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        replica.SuppressReply = null;
        using var next = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
        await Assert.That(next.AsString()).IsEqualTo("replica");
        var ids = replica.ReceivedCommands.Select((command, index) => (command, index))
            .Where(item => item.command.StartsWith("XREAD "))
            .Select(item => replica.ReceivedConnectionIds[item.index]).ToArray();
        await Assert.That(ids.Length).IsEqualTo(2);
        await Assert.That(ids[0] != ids[1]).IsTrue(); // Cancellation discards the blocked socket.
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemovedReplicaDrainsBlockingLeaseUnlessClientDisposes(bool disposeClient)
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "local", true);
        FakeRespServer[] replicas = [replica];
        await using var sentinel = Sentinel(primary, () => Volatile.Read(ref replicas));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], false, RespireReadFrom.AzAffinity)
            with { SentinelPrimaryName = "primary" });
        var lease = await client.Core.ReadRouter.RentDedicatedConnectionAsync(RespireReadFrom.AzAffinity, CancellationToken.None, "local");
        lease.Pool.Return(lease.Connection);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.SuppressReply = command =>
        {
            if (!command.StartsWith("XREAD ")) return false;
            arrived.TrySetResult();
            return true;
        };
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 0, "STREAMS", "key", "0"]).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref replicas, []);
        await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!lease.Pool.IsStopping) await Task.Delay(5, limit.Token);
        await Assert.That(pending.IsCompleted).IsFalse();
        if (disposeClient)
        {
            await client.DisposeAsync();
            await Assert.That(async () => await pending.WaitAsync(limit.Token)).Throws<RespireConnectionException>();
        }
        else
        {
            var index = replica.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("XREAD "));
            await replica.SendRawAsync(Bulk("accepted"), replica.ReceivedConnectionIds[index]);
            using var reply = await pending.WaitAsync(limit.Token);
            await Assert.That(reply.AsString()).IsEqualTo("accepted");
            await lease.Pool.RetireAsync().AsTask().WaitAsync(limit.Token);
        }
        await Assert.That(lease.Connection.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DedicatedReplicaRoleWaitEndsOnCancellationOrDisposal(bool disposeClient)
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "local", true);
        var arrived = GateDedicatedRole(replica);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, RespireReadFrom.AzAffinity)
            with { CommandTimeout = null });
        using var caller = new CancellationTokenSource();
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 0, "STREAMS", "key", "0"], cancellationToken: caller.Token).AsTask();
        await arrived.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposeClient)
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<ObjectDisposedException>();
        }
        else
        {
            caller.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    [Test]
    public async Task RemovingReplicaDuringDedicatedRoleValidationReselects()
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "local", true);
        FakeRespServer[] replicas = [replica];
        await using var sentinel = Sentinel(primary, () => Volatile.Read(ref replicas));
        var arrived = GateDedicatedRole(replica);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], false, RespireReadFrom.AzAffinity)
            with { SentinelPrimaryName = "primary" });
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask();
        await arrived.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref replicas, []);
        await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);
        var index = replica.ReceivedCommands.ToList().FindLastIndex(command => command == "ROLE");
        await replica.SendRawAsync(replica.ReplyOverride!(replica.ReceivedConnectionIds[index], "ROLE")!, replica.ReceivedConnectionIds[index]);
        using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("primary");
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    private static Task GateDedicatedRole(FakeRespServer replica)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var roles = 0;
        replica.SuppressReply = command =>
        {
            if (command != "ROLE" || Interlocked.Increment(ref roles) != 2) return false;
            arrived.TrySetResult();
            return true;
        };
        return arrived.Task;
    }

    [Test, NotInParallel]
    public async Task WarmBlockingReplicaRentalAllocatesNothing()
    {
        await using var primary = Node("primary", "local", false);
        await using var replica = Node("replica", "local", true);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, RespireReadFrom.AzAffinity));
        var router = client.Core.ReadRouter;
        router.RoleRevalidationInterval = TimeSpan.FromDays(1);
        var first = await router.RentDedicatedConnectionAsync(RespireReadFrom.AzAffinity, CancellationToken.None, "local");
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
            var pending = router.RentDedicatedConnectionAsync(RespireReadFrom.AzAffinity, CancellationToken.None, "local");
            if (!pending.IsCompletedSuccessfully) throw new InvalidOperationException("Warm rental performed asynchronous work.");
            var lease = pending.Result;
            lease.Pool.Return(lease.Connection);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
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
            UseCluster = cluster, Protocol = RespProtocol.Resp2, Connections = 1,
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
