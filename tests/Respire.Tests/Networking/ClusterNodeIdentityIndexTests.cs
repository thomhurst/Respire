using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterNodeIdentityIndexTests
{
    [Test]
    public async Task DetachingGenerationsPrunesIdentitiesAndPreservesConfiguredSeeds()
    {
        var seedEndpoint = new RespireEndpoint("seed.example");
        var endpoint = new RespireEndpoint("node.example");
        await using var seed = RespireConnectionMultiplexer.Create(seedEndpoint.Host, seedEndpoint.Port);
        await using var first = RespireConnectionMultiplexer.Create(endpoint.Host, endpoint.Port);
        await using var second = RespireConnectionMultiplexer.Create(endpoint.Host, endpoint.Port);
        var created = 0;
        var gate = new object();
        var index = new ClusterNodeIdentityIndex(seedEndpoint, seed, _ => ++created == 1 ? first : second, gate);
        WithLock(gate, () => index.ApplySnapshot([new(0, 16383, endpoint, "old", [])]));
        WithLock(gate, () => index.ApplySnapshot([new(0, 16383, endpoint, "new", [])]));

        var detached = WithLock(gate, () => index.DetachInactive([second], [seedEndpoint]));
        await Assert.That(detached).IsEquivalentTo([first]);
        await Assert.That(WithLock(gate, () => index.IsActive(first))).IsFalse();
        await Assert.That(WithLock(gate, () => index.GetOrCreate(seedEndpoint))).IsSameReferenceAs(seed);
        await Assert.That(WithLock(gate, () => index.NodeIdCount)).IsEqualTo(1);
        await Assert.That(WithLock(gate, () => index.ReverseNodeIdCount)).IsEqualTo(1);
        await Assert.That(WithLock(gate, () => index.All.Count())).IsEqualTo(3);
        await first.RetireAsync();
        lock (gate) index.Forget(first);
        await Assert.That(WithLock(gate, () => index.All.Count())).IsEqualTo(2);
        await Assert.That(WithLock(gate, () => index.GetOrCreate(endpoint))).IsSameReferenceAs(second);
    }

    [Test]
    public async Task RetainedSeedWithoutSlotsLosesItsOldNodeIdentity()
    {
        var seedEndpoint = new RespireEndpoint("seed.example");
        var endpoint = new RespireEndpoint("node.example");
        await using var seed = RespireConnectionMultiplexer.Create(seedEndpoint.Host, seedEndpoint.Port);
        await using var replacement = RespireConnectionMultiplexer.Create(endpoint.Host, endpoint.Port);
        var gate = new object();
        var index = new ClusterNodeIdentityIndex(seedEndpoint, seed, _ => replacement, gate);
        WithLock(gate, () => index.ApplySnapshot([new(0, 16383, seedEndpoint, "old", [])]));
        WithLock(gate, () => index.ApplySnapshot([new(0, 16383, endpoint, "new", [])]));
        var detached = WithLock(gate, () => index.DetachInactive([replacement], [seedEndpoint]));
        await Assert.That(detached).IsEmpty();
        await Assert.That(WithLock(gate, () => index.NodeIdCount)).IsEqualTo(1);
        await Assert.That(WithLock(gate, () => index.ReverseNodeIdCount)).IsEqualTo(1);
        await Assert.That(WithLock(gate, () => index.GetOrCreate(seedEndpoint))).IsSameReferenceAs(seed);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PreferredEndpointsAreReservedBeforeAliasResolution(bool reverse, bool includeIds)
    {
        await using var server = new FakeRespServer();
        var firstEndpoint = new RespireEndpoint("node-a.example", server.Port);
        var secondEndpoint = new RespireEndpoint("127.0.0.1", server.Port);
        await using var first = RespireConnectionMultiplexer.Create(firstEndpoint.Host, firstEndpoint.Port);
        await using var second = await RespireConnectionMultiplexer.CreateAsync(secondEndpoint.Host, secondEndpoint.Port);
        await using var duplicate = RespireConnectionMultiplexer.Create(secondEndpoint.Host, secondEndpoint.Port);
        var gate = new object();
        var index = new ClusterNodeIdentityIndex(secondEndpoint, second,
            endpoint => endpoint == firstEndpoint ? first : duplicate, gate);
        List<ClusterTopologyRange> ranges = [
            new(0, 1, firstEndpoint, includeIds ? "first" : null, [secondEndpoint]),
            new(2, 3, secondEndpoint, includeIds ? "second" : null, []),
        ];
        if (reverse) ranges.Reverse();
        RespireConnectionMultiplexer firstOwner;
        RespireConnectionMultiplexer secondOwner;
        lock (gate)
        {
            var resolved = index.ApplySnapshot(ranges);
            firstOwner = resolved.Single(entry => entry.Range.Preferred == firstEndpoint).Node;
            secondOwner = resolved.Single(entry => entry.Range.Preferred == secondEndpoint).Node;
        }

        await Assert.That(ReferenceEquals(firstOwner, first)).IsTrue();
        await Assert.That(ReferenceEquals(secondOwner, second)).IsTrue();
    }

    [Test]
    public async Task UnconnectedAliasDoesNotOverridePreferredTransport()
    {
        var aliasEndpoint = new RespireEndpoint("127.0.0.1");
        var preferredEndpoint = new RespireEndpoint("redis.example");
        await using var alias = RespireConnectionMultiplexer.Create(aliasEndpoint.Host, aliasEndpoint.Port);
        await using var preferred = RespireConnectionMultiplexer.Create(preferredEndpoint.Host, preferredEndpoint.Port);
        var gate = new object();
        var index = new ClusterNodeIdentityIndex(aliasEndpoint, alias, _ => preferred, gate);

        var resolved = WithLock(gate, () => index.ApplySnapshot([new(0, 3, preferredEndpoint, "node", [aliasEndpoint])]));

        await Assert.That(ReferenceEquals(resolved[0].Node, preferred)).IsTrue();
        await Assert.That(ReferenceEquals(WithLock(gate, () => index.GetOrCreate(aliasEndpoint)), preferred)).IsTrue();
    }

    [Test]
    public async Task WithdrawnAliasNoLongerSubstitutesRetiredTransportHost()
    {
        var oldEndpoint = new RespireEndpoint("old.example");
        var newEndpoint = new RespireEndpoint("new.example");
        var withdrawn = new RespireEndpoint("alias.example");
        await using var oldNode = RespireConnectionMultiplexer.Create(oldEndpoint.Host, oldEndpoint.Port);
        await using var newNode = RespireConnectionMultiplexer.Create(newEndpoint.Host, newEndpoint.Port);
        await using var aliasNode = RespireConnectionMultiplexer.Create(withdrawn.Host, withdrawn.Port);
        var gate = new object();
        var index = new ClusterNodeIdentityIndex(oldEndpoint, oldNode,
            endpoint => endpoint == newEndpoint ? newNode : aliasNode, gate);
        WithLock(gate, () => index.ApplySnapshot([new(0, 3, oldEndpoint, "node", [withdrawn])]));
        await Assert.That(ReferenceEquals(WithLock(gate, () => index.GetOrCreate(withdrawn)), oldNode)).IsTrue();

        WithLock(gate, () => index.ApplySnapshot([new(0, 3, newEndpoint, "node", [])]));

        await Assert.That(WithLock(gate, () => index.Endpoints.Contains(withdrawn))).IsFalse();
        await Assert.That(ReferenceEquals(WithLock(gate, () => index.GetOrCreate(withdrawn)), aliasNode)).IsTrue();
        await Assert.That(WithLock(gate, () => index.All.ToArray())).IsEquivalentTo([oldNode, newNode, aliasNode]);
    }

    [Test]
    [Arguments(null, true, true)]
    [Arguments(null, false, false)]
    [Arguments("known-id", true, true)]
    [Arguments("known-id", false, false)]
    [Arguments("conflicting-id", true, false)]
    [Arguments("conflicting-id", false, false)]
    public async Task NodeIdAndAdvertisedEndpointDetermineReuse(string? nodeId, bool endpointKnown, bool reuse)
    {
        var known = new RespireEndpoint("known.example");
        var unknown = new RespireEndpoint("unknown.example");
        await using var primary = RespireConnectionMultiplexer.Create(known.Host, known.Port);
        var requested = endpointKnown ? known : unknown;
        await using var replacement = RespireConnectionMultiplexer.Create(requested.Host, requested.Port);
        var gate = new object();
        var index = new ClusterNodeIdentityIndex(known, primary, _ => replacement, gate);
        WithLock(gate, () => index.ApplySnapshot([new(0, 3, known, "known-id", [])]));

        var resolved = WithLock(gate, () => index.ApplySnapshot([new(0, 3, requested, nodeId, [])]));

        await Assert.That(ReferenceEquals(resolved[0].Node, primary)).IsEqualTo(reuse);
        await Assert.That(ReferenceEquals(WithLock(gate, () => index.GetOrCreate(requested)), resolved[0].Node)).IsTrue();
    }

    [Test]
    public async Task FailedSnapshotRetainsOwnershipWithoutPublishingAliasesOrIds()
    {
        var seed = new RespireEndpoint("seed.example");
        var created = new RespireEndpoint("created.example");
        await using var primary = RespireConnectionMultiplexer.Create(seed.Host, seed.Port);
        await using var candidate = RespireConnectionMultiplexer.Create(created.Host, created.Port);
        var gate = new object();
        var index = new ClusterNodeIdentityIndex(seed, primary, endpoint =>
            endpoint == created ? candidate : throw new InvalidOperationException("Injected construction failure"), gate);

        await Assert.That(() => WithLock(gate, () => index.ApplySnapshot([
            new(0, 1, created, "same-id", []),
            new(2, 3, new RespireEndpoint("failure.example"), "other-id", []),
        ]))).Throws<InvalidOperationException>();

        await Assert.That(WithLock(gate, () => index.Endpoints.ToArray())).IsEquivalentTo([seed]);
        await Assert.That(WithLock(gate, () => index.All.ToArray())).IsEquivalentTo([primary, candidate]);
        WithLock(gate, () => index.ApplySnapshot([new(0, 3, seed, "same-id", [])]));
        // A staged ID must not redirect the failed candidate to the later published seed.
        await Assert.That(ReferenceEquals(WithLock(gate, () => index.GetCurrent(candidate)), candidate)).IsTrue();
    }

    [Test]
    public async Task PreferredEndpointWinsWithoutNodeIdAndAliasSharesItsIdentity()
    {
        var preferredEndpoint = new RespireEndpoint("preferred.example");
        var aliasEndpoint = new RespireEndpoint("alias.example");
        await using var preferred = RespireConnectionMultiplexer.Create(preferredEndpoint.Host, preferredEndpoint.Port);
        await using var alias = RespireConnectionMultiplexer.Create(aliasEndpoint.Host, aliasEndpoint.Port);
        var gate = new object();
        var index = new ClusterNodeIdentityIndex(preferredEndpoint, preferred, _ => alias, gate);
        _ = WithLock(gate, () => index.GetOrCreate(aliasEndpoint));

        var resolved = WithLock(gate, () => index.ApplySnapshot([new(0, 3, preferredEndpoint, null, [aliasEndpoint])]));

        await Assert.That(ReferenceEquals(resolved[0].Node, preferred)).IsTrue();
        await Assert.That(ReferenceEquals(WithLock(gate, () => index.GetOrCreate(aliasEndpoint)), preferred)).IsTrue();
        await Assert.That(ReferenceEquals(WithLock(gate, () => index.GetCurrent(alias)), preferred)).IsTrue();
        await Assert.That(WithLock(gate, () => index.All.ToArray())).IsEquivalentTo([preferred, alias]);
    }

    private static T WithLock<T>(object gate, Func<T> action)
    {
        lock (gate)
        {
            return action();
        }
    }
}
