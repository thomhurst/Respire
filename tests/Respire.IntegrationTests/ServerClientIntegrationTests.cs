using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ServerClientIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConnectionControlsStayOnOneSocketAndNode(int protocol)
    {
        // PAUSE affects the whole server, so this test owns its container.
        await using var container = new RedisBuilder("redis:8.10.0").Build();
        await container.StartAsync();
        var options = new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            Connections = 2, AllowAdmin = true, ClientName = "admin-test",
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var first = await client.WithKeyPrefix("ignored:").Server.GetClientConnectionAsync();
        var second = await client.Server.GetClientConnectionAsync();
        first.Id.Should().NotBe(second.Id);
        (await first.GetNameAsync()).Should().Be("admin-test");
        (await first.InfoAsync()).Id.Should().Be(first.Id);
        (await second.InfoAsync()).Id.Should().Be(second.Id);
        await first.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "custom-library");
        await first.SetInfoAsync(RespireClientInfoAttribute.LibraryVersion, "1.2.3");
        await first.SetNoEvictAsync(true);
        await first.SetNoTouchAsync(true);
        var info = await first.InfoAsync();
        info.Attributes["lib-name"].Should().Be("custom-library");
        info.Attributes["lib-ver"].Should().Be("1.2.3");
        info.Flags.Should().Contain("e").And.Contain("T");
        var other = await second.InfoAsync();
        other.Flags.Should().NotContain("e").And.NotContain("T");
        await first.SetNoEvictAsync(false);
        await first.SetNoTouchAsync(false);
        (await first.InfoAsync()).Flags.Should().NotContain("e").And.NotContain("T");
        var tracking = await first.TrackingInfoAsync();
        tracking.Flags.Should().Contain("off");
        tracking.Prefixes.Should().BeEmpty();
        byte[] payload = [255, 0, 128];
        (await first.EchoAsync(payload)).Should().Equal(payload);
        var fanout = await client.Server.ClientsOnAllNodesAsync();
        fanout.Should().ContainSingle();
        fanout[0].Endpoint.Should().Be(first.Endpoint);
        fanout[0].Value.Select(row => row.Id).Should().Contain([first.Id, second.Id]);

        foreach (var mode in new[] { RespireClientPauseMode.Write, RespireClientPauseMode.All })
        {
            await first.PauseClientsAsync(TimeSpan.FromSeconds(2), mode);
            await first.UnpauseClientsAsync();
            (await first.EchoAsync(payload)).Should().Equal(payload);
        }
        (await first.UnblockClientAsync(long.MaxValue)).Should().BeFalse();
        await using var blocked = await RespireClient.ConnectAsync(options with { Connections = 1, ClientName = "blocked-test" });
        foreach (var mode in new[] { RespireClientUnblockMode.Timeout, RespireClientUnblockMode.Error })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pop = blocked.Lists.LeftPopAsync("missing", TimeSpan.FromSeconds(30), deadline.Token).AsTask();
            RespireServerClientInfo? waiting;
            do
            {
                waiting = (await client.Server.ClientsAsync(deadline.Token)).SingleOrDefault(row => row.Name == "blocked-test" && row.Command == "blpop");
                if (waiting is null) await Task.Delay(10, deadline.Token);
            } while (waiting is null);
            (await first.UnblockClientAsync(waiting.Id, mode, deadline.Token)).Should().BeTrue();
            if (mode == RespireClientUnblockMode.Timeout) (await pop).Should().BeNull();
            else
            {
                Func<Task> observe = async () => await pop;
                await observe.Should().ThrowAsync<RespireServerException>();
            }
        }
        await client.DisposeAsync();
        first.IsConnected.Should().BeFalse();
        info.Attributes["lib-name"].Should().Be("custom-library");
        tracking.Fields["flags"].Count.Should().BeGreaterThan(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Redis7PreservesUnsupportedCommandErrors(int protocol)
    {
        await using var container = new RedisBuilder("redis:7.0.15").Build();
        await container.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Connections = 1, AllowAdmin = true, Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
        });
        var connection = await client.Server.GetClientConnectionAsync();
        Func<Task> noTouch = async () => await connection.SetNoTouchAsync(true);
        Func<Task> setInfo = async () => await connection.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "name");
        await noTouch.Should().ThrowAsync<RespireServerException>();
        await setInfo.Should().ThrowAsync<RespireServerException>();
        (await connection.GetNameAsync()).Should().BeNull();
        (await connection.InfoAsync()).Id.Should().Be(connection.Id);
        await connection.SetNoEvictAsync(true);
        (await connection.InfoAsync()).Flags.Should().Contain("e");
    }

    [Test]
    public async Task TrackingInspectionPreservesClientSideCaching()
    {
        await using var container = new RedisBuilder("redis:8.10.0").Build();
        await container.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Connections = 1, ClientSideCache = new(),
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
        });
        var connection = await client.Server.GetClientConnectionAsync();
        var before = await connection.TrackingInfoAsync();
        before.Flags.Should().Contain(["on", "optin"]);
        await client.Strings.SetAsync("cached", "value");
        (await client.Strings.GetAsync<string>("cached")).Should().Be("value");
        var after = await connection.TrackingInfoAsync();
        after.Flags.Should().BeEquivalentTo(before.Flags);
        after.RedirectClientId.Should().Be(before.RedirectClientId);
        (await client.Strings.GetAsync<string>("cached")).Should().Be("value");
    }
}


[ClassDataSource<ClusterTransactionTestContainer>(Shared = SharedType.PerTestSession)]
public class ServerClientClusterIntegrationTests(ClusterTransactionTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ClusterHandlesAndClientListsRetainEndpointIdentity(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], UseCluster = true, Connections = 2,
            Protocol = (RespProtocol)protocol, ClientName = $"cluster-admin-{protocol}",
        });
        var handle = await client.Server.GetClientConnectionAsync();
        (await handle.InfoAsync()).Id.Should().Be(handle.Id);
        var nodes = await client.Server.ClientsOnAllNodesAsync();
        nodes.Should().ContainSingle();
        nodes[0].Endpoint.Should().Be(handle.Endpoint);
        nodes[0].Value.Should().Contain(row => row.Id == handle.Id && row.Name == $"cluster-admin-{protocol}");
    }
}
