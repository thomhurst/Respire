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
    public async Task WarmZoneSelectionAndCounterAllocateNothing()
    {
        await using var primary = Node("primary", "remote", false);
        await using var replica = Node("replica", "local", true);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], false, RespireReadFrom.AzAffinity)
            with { ReplicaRefreshInterval = TimeSpan.FromMinutes(5) });
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
                _ when command.StartsWith("GET ") => Bulk(name),
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
