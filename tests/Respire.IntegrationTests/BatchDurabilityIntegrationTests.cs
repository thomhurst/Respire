using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class BatchDurabilityIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReplicationAndAofAcknowledgementsCoverTheQueuedWrites(int protocol)
    {
        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync();
        await using var primary = new ContainerBuilder("redis:8.4-alpine")
            .WithNetwork(network).WithNetworkAliases("primary").WithPortBinding(6379, true)
            .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always", "--save", "", "--protected-mode", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await primary.StartAsync();
        await using var replica = new ContainerBuilder("redis:8.4-alpine")
            .WithNetwork(network).WithPortBinding(6379, true)
            .WithCommand("redis-server", "--replicaof", "primary", "6379", "--appendonly", "yes", "--appendfsync", "always", "--save", "", "--protected-mode", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await replica.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var info = await replica.ExecAsync(["redis-cli", "INFO", "replication"], timeout.Token);
            if (info.Stdout.Contains("master_link_status:up", StringComparison.Ordinal)) break;
            await Task.Delay(100, timeout.Token);
        }
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(primary.Hostname, primary.GetMappedPublicPort(6379))], Connections = 3,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
        });
        await using var reader = await RespireClient.ConnectAsync($"redis://{replica.Hostname}:{replica.GetMappedPublicPort(6379)}?protocol={protocol}");
        var view = client.WithKeyPrefix("tenant:");
        using var replicated = view.CreateBatch();
        var first = replicated.Set("first", "one");
        var second = replicated.Set("second", "two");
        (await replicated.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(5), timeout.Token)).Should().BeGreaterThanOrEqualTo(1);
        first.Result.Should().BeTrue();
        second.Result.Should().BeTrue();
        (await reader.GetStringAsync("tenant:first")).Should().Be("one");
        (await reader.GetStringAsync("tenant:second")).Should().Be("two");
        using var persisted = view.CreateBatch();
        byte[] bytes = [0xff, 0, 1];
        var binary = persisted.Set("binary", bytes);
        var counts = await persisted.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.FromSeconds(5), timeout.Token);
        counts.Local.Should().Be(1);
        counts.Replicas.Should().BeGreaterThanOrEqualTo(1);
        binary.Result.Should().BeTrue();
        (await reader.GetAsync<byte[]>("tenant:binary")).Should().Equal(bytes);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InsufficientCountsAreResultsAndDoNotUndoWrites(int protocol)
    {
        await using var server = new ContainerBuilder("redis:8.4-alpine").WithPortBinding(6379, true)
            .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always", "--save", "")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await server.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(server.Hostname, server.GetMappedPublicPort(6379))], Connections = 2,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            ClientSideCache = protocol == 3 ? new() : null,
        });
        await client.SetAsync("first", "old");
        (await client.GetStringAsync("first")).Should().Be("old");
        using var replicated = client.CreateBatch();
        var first = replicated.Set("first", "one");
        (await replicated.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromMilliseconds(50))).Should().Be(0);
        first.Result.Should().BeTrue();
        (await client.GetStringAsync("first")).Should().Be("one");
        using var persisted = client.CreateBatch();
        var second = persisted.Set("second", "two");
        (await persisted.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.FromMilliseconds(50))).Should().Be(new RespireAofAcknowledgement(1, 0));
        second.Result.Should().BeTrue();
        (await client.GetStringAsync("second")).Should().Be("two");
        using var noReplicaRequirement = client.CreateBatch();
        _ = noReplicaRequirement.Set("third", "three");
        (await noReplicaRequirement.ExecuteAndWaitForReplicationAsync(0, TimeSpan.Zero)).Should().Be(0);
    }

    [Test]
    [Arguments("redis:7.0.15", 2)]
    [Arguments("redis:7.0.15", 3)]
    [Arguments("redis:8.4-alpine", 2)]
    [Arguments("redis:8.4-alpine", 3)]
    public async Task UnsupportedAofConfigurationKeepsServerErrorAndWriteResult(string image, int protocol)
    {
        await using var server = new ContainerBuilder(image).WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await server.StartAsync();
        await using var client = await RespireClient.ConnectAsync($"redis://{server.Hostname}:{server.GetMappedPublicPort(6379)}?protocol={protocol}");
        using var batch = client.CreateBatch();
        var write = batch.Set("key", "written");
        Func<Task> execute = async () => { await batch.ExecuteAndWaitForAofAsync(true, 0, TimeSpan.FromSeconds(1)); };
        await execute.Should().ThrowAsync<RespireServerException>();
        write.Result.Should().BeTrue();
        (await client.GetStringAsync("key")).Should().Be("written");
        if (image == "redis:8.4-alpine")
        {
            using var withoutLocalRequirement = client.CreateBatch();
            _ = withoutLocalRequirement.Set("next", "written");
            (await withoutLocalRequirement.ExecuteAndWaitForAofAsync(false, 0, TimeSpan.Zero))
                .Should().Be(new RespireAofAcknowledgement(0, 0));
        }
    }
}
