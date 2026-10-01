using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

public sealed class SentinelFailoverIntegrationTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task ClientMovesToPromotedPrimaryAfterCurrentPrimaryStops(RespireContainerServer server)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Topology = RespireContainerTopology.Sentinel,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp2,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }, deadline.Token);
        var formerPrimary = client.Endpoint;
        (await client.SetAsync("before-failover", "value", cancellationToken: deadline.Token)).Should().BeTrue();

        await using (var admin = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [formerPrimary],
            Protocol = RespProtocol.Resp2,
            AllowAdmin = true,
            Connections = 1,
        }, deadline.Token))
        {
            try
            {
                using var result = await admin.ExecuteAsync(
                    RespireCommands.Server.SHUTDOWN, ["NOSAVE"], cancellationToken: deadline.Token);
            }
            catch (RespireException)
            {
                // SHUTDOWN closes its own connection before it can return a normal reply.
            }
        }

        while (client.Endpoint == formerPrimary)
            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);

        client.Endpoint.Should().NotBe(formerPrimary);
        (await client.SetAsync("after-failover", "value", cancellationToken: deadline.Token)).Should().BeTrue();
        (await client.GetStringAsync("after-failover", deadline.Token)).Should().Be("value");
        await client.PingAsync(deadline.Token);
    }

    [Test]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task ClientNeverWritesToFormerPrimaryAfterPromotion(RespireContainerServer server)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Topology = RespireContainerTopology.Sentinel,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp2,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }, deadline.Token);
        var formerPrimary = client.Endpoint;

        await using (var sentinel = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.SentinelEndpoints[0]],
            Protocol = RespProtocol.Resp2,
            AllowAdmin = true,
        }, deadline.Token))
        {
            using var result = await sentinel.ExecuteAsync(
                RespireCommands.Sentinel.SENTINEL_FAILOVER,
                [RespireContainerFixture.SentinelServiceName],
                cancellationToken: deadline.Token);
        }

        while (client.Endpoint == formerPrimary)
            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        client.Endpoint.Should().NotBe(formerPrimary);
        (await client.SetAsync("promoted-write", "value", cancellationToken: deadline.Token)).Should().BeTrue();

        await using var oldPrimary = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [formerPrimary],
            Protocol = RespProtocol.Resp2,
        }, deadline.Token);
        while (true)
        {
            try
            {
                using var role = await oldPrimary.ExecuteAsync(RespireCommands.Server.ROLE, [],
                    cancellationToken: deadline.Token);
                if (role[0].AsString() is "slave" or "replica") break;
            }
            catch (RespireConnectionException) { }
            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        }
        Func<Task> writeToOldPrimary = async () =>
            await oldPrimary.SetAsync("must-not-write-to-old-primary", "value", cancellationToken: deadline.Token);
        var error = await writeToOldPrimary.Should().ThrowAsync<RespireServerException>();
        error.Which.Code.Should().Be("READONLY");
        (await client.GetStringAsync("promoted-write", deadline.Token)).Should().Be("value");
    }
}
