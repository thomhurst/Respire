using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class ServerHotKeysIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [MatrixDataSource]
    public async Task LifecycleIsSharedPerNodeAndKeepsOwnedBinaryRankings([Matrix(2, 3)] int protocol,
        [Matrix(RespireHotKeysMetrics.Cpu, RespireHotKeysMetrics.Network, RespireHotKeysMetrics.Cpu | RespireHotKeysMetrics.Network)] RespireHotKeysMetrics metrics)
    {
        // HOTKEYS has a single node-wide session, so every case owns its server.
        await using var container = new RedisBuilder("redis:8.6-alpine").Build();
        await container.StartAsync();
        var address = $"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}&allowAdmin=true&connections=2";
        await using var client = await RespireClient.ConnectAsync(address);
        await using var other = await RespireClient.ConnectAsync(address);
        var tracker = await client.WithKeyPrefix("ignored:").Server.GetHotKeysTrackerAsync();
        var peer = await other.Server.GetHotKeysTrackerAsync();
        tracker.Endpoint.Port.Should().Be(container.GetMappedPublicPort(6379));
        (await tracker.GetAsync()).Should().BeNull();
        (await tracker.StopAsync()).Should().BeFalse();
        await tracker.ResetAsync();
        await tracker.StartAsync(new() { Metrics = metrics, Count = 3, SampleRatio = 1 });
        Func<Task> duplicate = async () => await peer.StartAsync(new());
        await duplicate.Should().ThrowAsync<RespireServerException>().WithMessage("*already in progress*");
        Func<Task> prematureReset = async () => await peer.ResetAsync();
        await prematureReset.Should().ThrowAsync<RespireServerException>().WithMessage("*stop tracking first*");
        byte[] key = [0, 255, 32], value = [254, 0, 32];
        for (var index = 0; index < 64; index++)
        {
            await client.SetAsync(key, value);
            (await client.GetBytesAsync(key)).Should().Equal(value);
        }
        var active = (await tracker.GetAsync())!.Single();
        active.TrackingActive.Should().BeTrue();
        active.SampleRatio.Should().Be(1);
        active.CollectionStartUnixMilliseconds.Should().BeGreaterThan(0);
        active.SelectedSlots.Should().Equal(new RespireHotKeysSlotRange(0, 16383));
        active.SampledCommandsSelectedSlotsMicroseconds.Should().BeNull();
        if ((metrics & RespireHotKeysMetrics.Cpu) != 0)
        {
            active.ByCpuTime.Should().NotBeNull();
            active.ByCpuTime!.Should().ContainSingle();
            active.ByCpuTime[0].Key.Should().Equal(key);
            active.ByCpuTime[0].Microseconds.Should().BeGreaterThan(0);
            active.TotalCpuUserMilliseconds.Should().BeGreaterThanOrEqualTo(0);
            active.TotalCpuSystemMilliseconds.Should().BeGreaterThanOrEqualTo(0);
        }
        else { active.ByCpuTime.Should().BeNull(); active.TotalCpuUserMilliseconds.Should().BeNull(); }
        if ((metrics & RespireHotKeysMetrics.Network) != 0)
        {
            active.ByNetworkBytes!.Should().ContainSingle();
            active.ByNetworkBytes[0].Key.Should().Equal(key);
            active.ByNetworkBytes[0].Bytes.Should().BeGreaterThan(0);
            active.TotalNetworkBytes.Should().BeGreaterThan(0);
        }
        else { active.ByNetworkBytes.Should().BeNull(); active.TotalNetworkBytes.Should().BeNull(); }
        // A different connection sees and stops the same server session.
        (await peer.StopAsync()).Should().BeTrue();
        (await tracker.StopAsync()).Should().BeFalse();
        (await peer.GetAsync())!.Single().TrackingActive.Should().BeFalse();
        var fanOut = await client.Server.GetHotKeysOnAllNodesAsync();
        fanOut.Should().ContainSingle();
        fanOut[0].Endpoint.Should().Be(tracker.Endpoint);
        fanOut[0].Value!.Single().TrackingActive.Should().BeFalse();
        (await client.Server.ResetHotKeysOnAllNodesAsync()).Single().Value.Should().BeTrue();
        (await tracker.GetAsync()).Should().BeNull();
        (await client.Server.StartHotKeysOnAllNodesAsync(new() { Metrics = RespireHotKeysMetrics.Network, SampleRatio = 2 })).Single().Value.Should().BeTrue();
        (await peer.GetAsync())!.Single().SampleRatio.Should().Be(2);
        (await client.Server.StopHotKeysOnAllNodesAsync()).Single().Value.Should().BeTrue();
        (await client.Server.StopHotKeysOnAllNodesAsync()).Single().Value.Should().BeFalse();
        await peer.ResetAsync();
        (await client.Server.GetHotKeysOnAllNodesAsync()).Single().Value.Should().BeNull();

        await client.Server.AclSetUserAsync("reader", ["reset", "on", ">hotkeys-test", "+ping"]);
        await using var restricted = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [tracker.Endpoint], Protocol = (RespProtocol)protocol, Connections = 1,
            Username = "reader", Password = "hotkeys-test",
        });
        var restrictedTracker = await restricted.Server.GetHotKeysTrackerAsync();
        Func<Task> denied = async () => await restrictedTracker.GetAsync();
        (await denied.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("NOPERM");
        await client.DisposeAsync();
        if (active.ByCpuTime is { } cpu) cpu[0].Key.Should().Equal(key);
        if (active.ByNetworkBytes is { } network) network[0].Key.Should().Equal(key);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DurationStopsAutomaticallyAndStandaloneSlotsRemainServerErrors(int protocol)
    {
        await using var container = new RedisBuilder("redis:8.6-alpine").Build();
        await container.StartAsync();
        await using var client = await RespireClient.ConnectAsync($"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}&allowAdmin=true");
        var tracker = await client.Server.GetHotKeysTrackerAsync();
        Func<Task> slots = async () => await tracker.StartAsync(new() { Slots = new[] { 1 } });
        await slots.Should().ThrowAsync<RespireServerException>().WithMessage("*non-cluster*");
        await tracker.StartAsync(new() { DurationSeconds = 1 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RespireHotKeysSnapshot snapshot;
        do
        {
            await Task.Delay(25, deadline.Token);
            snapshot = (await tracker.GetAsync(deadline.Token))!.Single();
        } while (snapshot.TrackingActive);
        snapshot.CollectionDurationMilliseconds.Should().BeGreaterThanOrEqualTo(1000);
        (await tracker.StopAsync()).Should().BeFalse();
        await tracker.ResetAsync();
        (await tracker.GetAsync()).Should().BeNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ClusterSlotsAndSamplingPreserveLiteralRanges(int protocol)
    {
        await using var container = new ContainerBuilder("redis:8.6-alpine")
            .WithPortBinding(6379, true).WithCommand("redis-server", "--cluster-enabled", "yes", "--cluster-config-file", "nodes.conf",
                "--cluster-announce-ip", "127.0.0.1", "--appendonly", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await container.StartAsync();
        var port = container.GetMappedPublicPort(6379);
        await Run(["redis-cli", "-e", "CONFIG", "SET", "cluster-announce-port", port.ToString()]);
        await Run(["redis-cli", "-e", "CLUSTER", "ADDSLOTSRANGE", "0", "16383"]);
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var state = await container.ExecAsync(["redis-cli", "CLUSTER", "INFO"], ready.Token);
            if (state.Stdout.Contains("cluster_state:ok", StringComparison.Ordinal)) break;
            await Task.Delay(50, ready.Token);
        }
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(container.Hostname, port)], UseCluster = true, Protocol = (RespProtocol)protocol, Connections = 1, AllowAdmin = true,
        });
        var tracker = await client.WithKeyPrefix("ignored:").Server.GetHotKeysTrackerAsync();
        await tracker.StartAsync(new() { Slots = new[] { 3, 1, 2, 16383 }, SampleRatio = 2 });
        var snapshot = (await tracker.GetAsync())!.Single();
        snapshot.SelectedSlots.Should().Equal(new RespireHotKeysSlotRange(1, 3), new RespireHotKeysSlotRange(16383, 16383));
        snapshot.SampledCommandsSelectedSlotsMicroseconds.Should().Be(0);
        snapshot.NetworkBytesSampledCommandsSelectedSlots.Should().Be(0);
        snapshot.AllCommandsSelectedSlotsMicroseconds.Should().Be(0);
        snapshot.NetworkBytesAllCommandsSelectedSlots.Should().Be(0);
        var results = await client.Server.GetHotKeysOnAllNodesAsync();
        results.Should().ContainSingle();
        results[0].Endpoint.Port.Should().Be(port);
        results[0].Value!.Single().SelectedSlots.Should().Equal(snapshot.SelectedSlots);
        await tracker.StopAsync(); await tracker.ResetAsync();
        async Task Run(string[] command)
        {
            var result = await container.ExecAsync(command);
            result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnsupportedRedisErrorsArePreserved(int protocol)
    {
        // Unsupported commands change nothing, so the shared Redis 7.2 server is safe.
        var lease = await servers.LeaseAsync("redis:7.2-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol) + "&allowAdmin=true");
        var tracker = await client.Server.GetHotKeysTrackerAsync();
        Func<Task>[] commands = [async () => await tracker.StartAsync(new()), async () => await tracker.GetAsync(),
            async () => await tracker.StopAsync(), async () => await tracker.ResetAsync()];
        foreach (var command in commands) await command.Should().ThrowAsync<RespireServerException>();
        (await client.Server.GetHotKeysOnAllNodesAsync()).Single().Error.Should().BeOfType<RespireServerException>();
        (await client.Server.StartHotKeysOnAllNodesAsync(new())).Single().Error.Should().BeOfType<RespireServerException>();
        (await client.Server.StopHotKeysOnAllNodesAsync()).Single().Error.Should().BeOfType<RespireServerException>();
        (await client.Server.ResetHotKeysOnAllNodesAsync()).Single().Error.Should().BeOfType<RespireServerException>();
    }
}
