using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

[NotInParallel]
public class SentinelClientIntegrationTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task ReadsAndAdministersExplicitSentinel(RespireContainerServer server)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        // Each case owns a container with private server processes; failover never targets a shared fixture.
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server, Topology = RespireContainerTopology.Sentinel,
        }, deadline.Token);
        await using var client = await RespireSentinelClient.ConnectAsync(fixture.SentinelEndpoints[0],
            fixture.CreateOptions() with { AllowAdmin = true }, deadline.Token);
        var name = RespireContainerFixture.SentinelServiceName;
        var primary = await client.PrimaryAsync(name, deadline.Token);
        primary.Name.Should().Be(name);
        primary.Quorum.Should().BeGreaterThan(0);
        (await client.PrimariesAsync(deadline.Token)).Should().Contain(item => item.Name == name);
        (await client.ReplicasAsync(name, deadline.Token)).Should().NotBeEmpty();
        // Fixture readiness requires quorum, which can precede discovery of every peer.
        var peers = await client.SentinelsAsync(name, deadline.Token);
        while (peers.Length < fixture.SentinelEndpoints.Count - 1)
        {
            await Task.Delay(100, deadline.Token);
            peers = await client.SentinelsAsync(name, deadline.Token);
        }
        peers.Should().HaveCount(fixture.SentinelEndpoints.Count - 1);
        (await client.MyIdAsync(deadline.Token)).Should().HaveLength(40);
        (await client.CheckQuorumAsync(name, deadline.Token)).Should().StartWith("OK");
        (await client.IsPrimaryDownByAddressAsync(primary.Endpoint, cancellationToken: deadline.Token)).IsDown.Should().BeFalse();
        (await client.InfoCacheAsync([name], deadline.Token)).Should().NotBeEmpty();
        (await client.PendingScriptsAsync(deadline.Token)).Should().BeEmpty();
        var config = await client.ConfigGetAsync("resolve-hostnames", deadline.Token);
        await client.ConfigSetAsync(config, deadline.Token);
        await client.SetAsync(name, new Dictionary<string, string> { ["parallel-syncs"] = "1" }, deadline.Token);
        await client.FlushConfigAsync(deadline.Token);
        await client.SimulateFailureAsync(RespireSentinelFailure.CrashAfterElection | RespireSentinelFailure.CrashAfterPromotion, deadline.Token);
        await client.SimulateFailureAsync(RespireSentinelFailure.None, deadline.Token);

        const string temporary = "typed-sentinel-admin";
        await client.MonitorAsync(temporary, primary.Endpoint, primary.Quorum, deadline.Token);
        (await client.ResetAsync(temporary, deadline.Token)).Should().Be(1);
        await client.RemoveAsync(temporary, deadline.Token);
        (await client.PrimariesAsync(deadline.Token)).Should().NotContain(item => item.Name == temporary);

        // Exercise real failover only inside this disposable fixture.
        await client.FailoverAsync(name, deadline.Token);
        RespireSentinelPrimary promoted;
        do
        {
            await Task.Delay(100, deadline.Token);
            promoted = await client.PrimaryAsync(name, deadline.Token);
        } while (promoted.Endpoint == primary.Endpoint);
        (await client.ReplicasAsync(name, deadline.Token)).Should().Contain(item => item.Endpoint == primary.Endpoint);
    }
}
