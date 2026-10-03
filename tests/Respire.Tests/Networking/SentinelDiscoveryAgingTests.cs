using Respire.Internal;
using Respire.Protocol;

namespace Respire.Tests.Networking;

public class SentinelDiscoveryAgingTests
{
    private static readonly RespireEndpoint Seed = new("seed", 26379);
    private static readonly RespireEndpoint Peer = new("peer", 26379);

    [Test]
    public async Task MissingFailedPeerIsRemovedAfterThreeDiscoveries()
    {
        var state = Create();
        state.RecordConnection(PeerMembership(state), succeeded: false);
        state.Snapshot(out var changed);
        Miss(state);
        Miss(state);
        await Assert.That(state.Snapshot().Length).IsEqualTo(2);
        Miss(state);
        await Assert.That(state.Snapshot().SequenceEqual([Seed])).IsTrue();
        await Assert.That(changed.IsCompleted).IsTrue();
    }

    [Test]
    public async Task HealthyPeerSurvivesUntilConnectionFails()
    {
        var state = Create();
        var membership = PeerMembership(state);
        state.RecordConnection(membership, succeeded: true);
        for (var i = 0; i < 5; i++) Miss(state);
        await Assert.That(state.Snapshot().Length).IsEqualTo(2);
        state.RecordConnection(membership, succeeded: false);
        await Assert.That(state.Snapshot().SequenceEqual([Seed])).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PeerReportOrSelfReportResetsMissingCount(bool selfReport)
    {
        var state = Create();
        state.RecordConnection(PeerMembership(state), succeeded: false);
        Miss(state);
        Miss(state);
        using (var round = state.BeginDiscovery())
        {
            round.Report(Seed, []);
            round.Report(selfReport ? new("PEER", 26379) : Seed, selfReport ? [] : [new("PEER", 26379)]);
        }
        Miss(state);
        Miss(state);
        await Assert.That(state.Snapshot().Length).IsEqualTo(2);
        Miss(state);
        await Assert.That(state.Snapshot().Length).IsEqualTo(1);
    }

    [Test]
    public async Task UnavailableAndCancelledDiscoveryDoNotAgePeers()
    {
        var state = Create();
        state.RecordConnection(PeerMembership(state), succeeded: false);
        for (var i = 0; i < 5; i++)
        {
            using (state.BeginDiscovery()) { }
            using var cancellation = new CancellationTokenSource();
            using var round = state.BeginDiscovery(cancellation.Token);
            round.Report(Seed, []);
            cancellation.Cancel();
        }
        Miss(state);
        Miss(state);
        await Assert.That(state.Snapshot().Length).IsEqualTo(2);
    }

    [Test]
    public async Task ConcurrentReportAndReaddedMembershipRejectOldEvidence()
    {
        var state = Create();
        var oldMembership = PeerMembership(state);
        state.RecordConnection(oldMembership, succeeded: false);
        Miss(state);
        Miss(state);
        using (var old = state.BeginDiscovery())
        {
            old.Report(Seed, []);
            using var newer = state.BeginDiscovery();
            newer.Report(Seed, [Peer]);
        }
        await Assert.That(state.Snapshot().Length).IsEqualTo(2);
        using (var old = state.BeginDiscovery())
        {
            old.Report(Seed, []);
            state.TryRemove(Peer);
            state.TryAdd(Peer);
        }
        state.RecordConnection(oldMembership, succeeded: false);
        for (var i = 0; i < 5; i++) Miss(state);
        await Assert.That(state.Snapshot().Length).IsEqualTo(2);
    }

    [Test]
    public async Task SuccessfulConnectionClearsFailureAndSeedsNeverAgeOut()
    {
        var state = Create();
        foreach (var membership in state.MembershipSnapshot(out _))
            state.RecordConnection(membership, succeeded: false);
        state.RecordConnection(PeerMembership(state), succeeded: true);
        for (var i = 0; i < 5; i++)
        {
            using var round = state.BeginDiscovery();
            round.Report(new("other", 26379), []);
        }
        await Assert.That(state.Snapshot().Length).IsEqualTo(2);
    }

    [Test]
    public async Task MonitorConnectionFailureRemovesPreviouslyMissingPeer()
    {
        var state = Create();
        for (var i = 0; i < 3; i++) Miss(state);
        state.Snapshot(out var changed);
        using var lifetime = new CancellationTokenSource();
        var seed = new SentinelMonitorProbe();
        var monitor = new SentinelMonitoring(new() { SentinelPrimaryName = "mymaster" }, null,
            new object(), state, lifetime, (_, _, _, _) => ValueTask.CompletedTask, (_, _, _) => { })
        {
            ClientFactory = options => options.Endpoints[0] == Seed ? seed.Client
                : throw new RespireConnectionException("Learned Sentinel is unavailable."),
        };
        try
        {
            monitor.Published();
            await changed.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(state.Snapshot().SequenceEqual([Seed])).IsTrue();
        }
        finally
        {
            var tasks = monitor.Stop();
            await lifetime.CancelAsync();
            await CleanupTasks.WhenAllAsync(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task AgingFreesCapacityAndPreservesEpochEvidence()
    {
        var state = new SentinelDiscoveryState([Seed]);
        for (var i = 0; i < SentinelDiscoveryState.MaximumDiscoveredEndpoints; i++)
            state.TryAdd(new($"peer-{i}", 26379));
        var primary = new RespireEndpoint("primary", 6379);
        state.AcceptConfiguration(primary, 12);
        await Assert.That(state.TryAdd(Peer)).IsFalse();
        var membership = state.MembershipSnapshot(out _)[1];
        state.RecordConnection(membership, succeeded: false);
        for (var i = 0; i < 3; i++) Miss(state);
        await Assert.That(state.TryAdd(Peer)).IsTrue();
        await Assert.That(state.IsCurrentConfiguration(primary, 12)).IsTrue();
        await Assert.That(state.TryObserveConfiguration(primary, 11)).IsFalse();
    }

    [Test]
    [Arguments("*0\r\n", true)]
    [Arguments("-NOPERM denied\r\n", false)]
    [Arguments("*1\r\n*0\r\n", false)]
    public async Task ResolverAgesOnlyWithValidPeerLists(string peerReply, bool removed)
    {
        var primaryReply = "*2\r\n$9\r\n127.0.0.1\r\n$4\r\n6379\r\n"u8.ToArray();
        await using var sentinel = new FakeRespServer(3, primaryReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => primaryReply,
                "SENTINEL SENTINELS mymaster" => System.Text.Encoding.ASCII.GetBytes(peerReply),
                _ => "*0\r\n"u8.ToArray(),
            },
        };
        var seed = new RespireEndpoint("127.0.0.1", sentinel.Port);
        var state = new SentinelDiscoveryState([seed]);
        state.TryAdd(Peer);
        state.RecordConnection(PeerMembership(state), succeeded: false);
        var options = new RespireOptions
        {
            Endpoints = [seed], SentinelPrimaryName = "mymaster", Protocol = RespProtocol.Resp2,
        };
        for (var i = 0; i < 3; i++)
            await SentinelResolver.ResolveAndConnectPrimaryAsync(options, (_, _, _) => ValueTask.FromResult(1),
                CancellationToken.None, state);
        await Assert.That(state.Snapshot().Length).IsEqualTo(removed ? 1 : 2);
    }

    private static SentinelDiscoveryState Create()
    {
        var state = new SentinelDiscoveryState([Seed]);
        state.TryAdd(Peer);
        return state;
    }

    private static SentinelDiscoveryState.Membership PeerMembership(SentinelDiscoveryState state)
        => state.MembershipSnapshot(out _).Single(item => item.Endpoint == Peer);

    private static void Miss(SentinelDiscoveryState state)
    {
        using var round = state.BeginDiscovery();
        round.Report(Seed, []);
    }
}
