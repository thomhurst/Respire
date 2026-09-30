using System.Text.RegularExpressions;
using Docker.DotNet;
using DotNet.Testcontainers.Configurations;
using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ContainerFixtureIntegrationTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis, RespireContainerTopology.Standalone)]
    [Arguments(RespireContainerServer.Redis, RespireContainerTopology.Cluster)]
    [Arguments(RespireContainerServer.Redis, RespireContainerTopology.Sentinel)]
    [Arguments(RespireContainerServer.Valkey, RespireContainerTopology.Standalone)]
    [Arguments(RespireContainerServer.Valkey, RespireContainerTopology.Cluster)]
    [Arguments(RespireContainerServer.Valkey, RespireContainerTopology.Sentinel)]
    public async Task PublicFixtureSupportsEveryServerAndTopology(RespireContainerServer server, RespireContainerTopology topology)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Server = server, Topology = topology });
        var disposableOptions = fixture.CreateOptions();
        disposableOptions.Endpoints.Clear();
        var options = fixture.CreateOptions();
        options.UseCluster.Should().Be(topology == RespireContainerTopology.Cluster);
        options.SentinelPrimaryName.Should().Be(topology == RespireContainerTopology.Sentinel ? RespireContainerFixture.SentinelServiceName : null);
        fixture.DataEndpoints.Should().HaveCount(topology switch
        {
            RespireContainerTopology.Cluster => 3,
            RespireContainerTopology.Sentinel => 2,
            _ => 1,
        });
        fixture.SentinelEndpoints.Should().HaveCount(topology == RespireContainerTopology.Sentinel ? 3 : 0);
        fixture.ContainerId.Should().NotBeNullOrWhiteSpace();
        using (var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig
            .GetDockerClientBuilder(Guid.NewGuid()).WithTimeout(TimeSpan.FromSeconds(5)).Build())
        {
            var container = await docker.Containers.InspectContainerAsync(fixture.ContainerId);
            (container.HostConfig?.Init).Should().BeTrue();
            var publishedPorts = container.NetworkSettings!.Ports.Values.SelectMany(bindings => bindings).ToArray();
            publishedPorts.Should().HaveCount(fixture.DataEndpoints.Count + fixture.SentinelEndpoints.Count);
            publishedPorts.Should().OnlyContain(binding => binding.HostIP == "127.0.0.1");
        }
        foreach (var endpoint in fixture.SentinelEndpoints)
        {
            // Assert readiness immediately: no test-side polling can hide early return.
            await using var sentinel = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [endpoint], Protocol = RespProtocol.Resp2, AllowAdmin = true,
            });
            using var primary = await sentinel.ExecuteAsync(RespireCommands.Sentinel.SENTINEL_GET_MASTER_ADDR_BY_NAME,
                RespireContainerFixture.SentinelServiceName);
            primary.Count.Should().Be(2);
            primary[0].AsString().Should().Be("127.0.0.1");
            primary[1].AsString().Should().Be(fixture.DataEndpoints[0].Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var replicas = await sentinel.ExecuteAsync(RespireCommands.Sentinel.SENTINEL_REPLICAS,
                RespireContainerFixture.SentinelServiceName);
            replicas.Count.Should().Be(1);
            var fields = new Dictionary<string, string>();
            for (var index = 0; index < replicas[0].Count; index += 2)
                fields.Add(replicas[0][index].AsString(), replicas[0][index + 1].AsString());
            fields["ip"].Should().Be("127.0.0.1");
            fields["port"].Should().Be(fixture.DataEndpoints[1].Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fields["flags"].Should().Be("slave");
        }
        await using (var client = await RespireClient.ConnectAsync(options with { Protocol = RespProtocol.Resp3 }))
        {
            // These hash tags cover all three primary slot ranges in the Cluster fixture.
            foreach (var key in new[] { "{a}:fixture", "{b}:fixture", "{c}:fixture" })
            {
                (await client.SetAsync(key, "value")).Should().BeTrue();
                (await client.GetStringAsync(key)).Should().Be("value");
            }
            if (topology == RespireContainerTopology.Sentinel)
            {
                await using var replica = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = { fixture.DataEndpoints[1] } });
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (await replica.GetStringAsync("{a}:fixture", deadline.Token) != "value")
                    await Task.Delay(50, deadline.Token);
            }
        }
        var containerId = fixture.ContainerId;
        var first = fixture.DisposeAsync().AsTask();
        var second = fixture.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);
        Action create = () => fixture.CreateOptions();
        create.Should().Throw<ObjectDisposedException>();
        await AssertContainerRemovedAsync(containerId);
    }

    [Test]
    public async Task SeparateFixturesDoNotShareDataOrCleanup()
    {
        await using var first = await RespireContainerFixture.StartAsync();
        await using var second = await RespireContainerFixture.StartAsync();
        first.DataEndpoints[0].Should().NotBe(second.DataEndpoints[0]);
        await using (var client = await RespireClient.ConnectAsync(first.CreateOptions()))
            await client.SetAsync("isolation", "first");
        await first.DisposeAsync();
        await using var survivor = await RespireClient.ConnectAsync(second.CreateOptions());
        (await survivor.GetStringAsync("isolation")).Should().BeNull();
        (await survivor.SetAsync("still-alive", "yes")).Should().BeTrue();
    }

    [Test]
    public async Task InvalidConfigurationAndPreCancellationFailBeforeDockerStartup()
    {
        foreach (var options in new[]
        {
            new RespireContainerOptions { StartupTimeout = TimeSpan.Zero },
            new RespireContainerOptions { Topology = (RespireContainerTopology)99 },
            new RespireContainerOptions { Server = (RespireContainerServer)99 },
            new RespireContainerOptions { Image = " " },
        })
        {
            Func<Task> start = async () => await RespireContainerFixture.StartAsync(options);
            await start.Should().ThrowAsync<ArgumentException>();
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> cancelledStart = async () => await RespireContainerFixture.StartAsync(cancellationToken: cancelled.Token);
        await cancelledStart.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task StartupDeadlineReportsItsStage()
    {
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new()
        {
            StartupTimeout = TimeSpan.FromMilliseconds(1),
        });
        await start.Should().ThrowAsync<TimeoutException>().WithMessage("*did not become ready*step:*");
    }

    [Test]
    public async Task IncompatibleImageReportsTheFailedBinaryAndCleansPartialStartup()
    {
        // Valid cached image, deliberately incompatible binary selection; no missing-image pull.
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new()
        {
            Server = RespireContainerServer.Valkey, Image = "redis:7.2-alpine",
        });
        var failure = await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*valkey-server*failed*");
        failure.Which.Data["RespireFixture.StartupStep"].Should().BeOfType<string>()
            .Which.Should().Contain("valkey-server");
        failure.Which.Data["RespireFixture.LastReadinessResponse"].Should().BeOfType<string>();
        var identity = Regex.Match(failure.Which.Message, @"Fixture container ([a-f0-9]{64}) command");
        identity.Success.Should().BeTrue();
        await AssertContainerRemovedAsync(identity.Groups[1].Value);
        // A new fixture must remain usable after the failed instance cleans up.
        await using var fixture = await RespireContainerFixture.StartAsync();
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        (await client.SetAsync("after-failure", "ok")).Should().BeTrue();
    }

    private static async Task AssertContainerRemovedAsync(string containerId)
    {
        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig
            .GetDockerClientBuilder(Guid.NewGuid()).WithTimeout(TimeSpan.FromSeconds(5)).Build();
        Func<Task> inspect = async () => await docker.Containers.InspectContainerAsync(containerId);
        await inspect.Should().ThrowAsync<DockerContainerNotFoundException>();
    }
}
