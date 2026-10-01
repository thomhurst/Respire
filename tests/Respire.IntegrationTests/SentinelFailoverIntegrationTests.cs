using System.Collections.Concurrent;
using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class SentinelFailoverIntegrationTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task SentinelNotificationMovesClientToPromotedPrimaryAndRejectsOldPrimary(
        RespireContainerServer serverFamily)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = serverFamily,
            Topology = RespireContainerTopology.Sentinel,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var options = fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp3,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            CommandTimeout = TimeSpan.FromSeconds(3),
        };
        await using var client = await RespireClient.ConnectAsync(options, deadline.Token);
        var stateChanges = new ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += stateChanges.Enqueue;
        var oldPrimary = fixture.DataEndpoints[0];
        var promotedPrimary = fixture.DataEndpoints[1];
        client.Endpoint.Should().Be(oldPrimary);
        (await client.SetAsync("sentinel-failover:before", "written", cancellationToken: deadline.Token)).Should().BeTrue();

        // Allow the router supervisor to attach its subscriptions to discovered Sentinels.
        await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);

        await fixture.StopDataNodeAsync(0, deadline.Token);
        while (!EndpointIs(client, promotedPrimary)) await Task.Delay(100, deadline.Token);
        while (!stateChanges.Any(change => change.Endpoint == promotedPrimary && change.State == RespireConnectionState.Connected))
            await Task.Delay(100, deadline.Token);

        (await client.SetAsync("sentinel-failover:after", "promoted", cancellationToken: deadline.Token)).Should().BeTrue();
        await fixture.StartDataNodeAsync(0, deadline.Token);
        await fixture.WaitForDataNodeReplicaAsync(0, deadline.Token);

        (await client.SetAsync("sentinel-failover:after-return", "still-promoted", cancellationToken: deadline.Token)).Should().BeTrue();
        client.Endpoint.Should().Be(promotedPrimary);

        // Disposal joins the Sentinel monitor tasks and closes their subscriptions.
        await client.DisposeAsync();
    }

    private static bool EndpointIs(IRespireClient client, RespireEndpoint endpoint)
    {
        try { return client.Endpoint == endpoint; }
        catch (InvalidOperationException) { return false; }
    }
}
