using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterNodeIdentityIndexTests
{
    [Test]
    public async Task UnconnectedAliasDoesNotOverridePreferredTransport()
    {
        var aliasEndpoint = new RespireEndpoint("127.0.0.1");
        var preferredEndpoint = new RespireEndpoint("redis.example");
        await using var alias = RespireConnectionMultiplexer.Create(aliasEndpoint.Host, aliasEndpoint.Port);
        await using var preferred = RespireConnectionMultiplexer.Create(preferredEndpoint.Host, preferredEndpoint.Port);
        var index = new ClusterNodeIdentityIndex(aliasEndpoint, alias, _ => preferred);

        var resolved = index.ApplySnapshot([new(0, 3, preferredEndpoint, "node", [aliasEndpoint])]);

        await Assert.That(ReferenceEquals(resolved[0].Node, preferred)).IsTrue();
        await Assert.That(ReferenceEquals(index.GetOrCreate(aliasEndpoint), preferred)).IsTrue();
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
        var index = new ClusterNodeIdentityIndex(oldEndpoint, oldNode,
            endpoint => endpoint == newEndpoint ? newNode : aliasNode);
        index.ApplySnapshot([new(0, 3, oldEndpoint, "node", [withdrawn])]);
        await Assert.That(ReferenceEquals(index.GetOrCreate(withdrawn), oldNode)).IsTrue();

        index.ApplySnapshot([new(0, 3, newEndpoint, "node", [])]);

        await Assert.That(index.Endpoints.Contains(withdrawn)).IsFalse();
        await Assert.That(ReferenceEquals(index.GetOrCreate(withdrawn), aliasNode)).IsTrue();
        await Assert.That(index.All).IsEquivalentTo([oldNode, newNode, aliasNode]);
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
        var index = new ClusterNodeIdentityIndex(known, primary, _ => replacement);
        index.ApplySnapshot([new(0, 3, known, "known-id", [])]);

        var resolved = index.ApplySnapshot([new(0, 3, requested, nodeId, [])]);

        await Assert.That(ReferenceEquals(resolved[0].Node, primary)).IsEqualTo(reuse);
        await Assert.That(ReferenceEquals(index.GetOrCreate(requested), resolved[0].Node)).IsTrue();
    }

    [Test]
    public async Task FailedSnapshotRetainsOwnershipWithoutPublishingAliasesOrIds()
    {
        var seed = new RespireEndpoint("seed.example");
        var created = new RespireEndpoint("created.example");
        await using var primary = RespireConnectionMultiplexer.Create(seed.Host, seed.Port);
        await using var candidate = RespireConnectionMultiplexer.Create(created.Host, created.Port);
        var index = new ClusterNodeIdentityIndex(seed, primary, endpoint =>
            endpoint == created ? candidate : throw new InvalidOperationException("Injected construction failure"));

        await Assert.That(() => index.ApplySnapshot([
            new(0, 1, created, "same-id", []),
            new(2, 3, new RespireEndpoint("failure.example"), "other-id", []),
        ])).Throws<InvalidOperationException>();

        await Assert.That(index.Endpoints).IsEquivalentTo([seed]);
        await Assert.That(index.All).IsEquivalentTo([primary, candidate]);
        index.ApplySnapshot([new(0, 3, seed, "same-id", [])]);
        // A staged ID must not redirect the failed candidate to the later published seed.
        await Assert.That(ReferenceEquals(index.GetCurrent(candidate), candidate)).IsTrue();
    }

    [Test]
    public async Task PreferredEndpointWinsWithoutNodeIdAndAliasSharesItsIdentity()
    {
        var preferredEndpoint = new RespireEndpoint("preferred.example");
        var aliasEndpoint = new RespireEndpoint("alias.example");
        await using var preferred = RespireConnectionMultiplexer.Create(preferredEndpoint.Host, preferredEndpoint.Port);
        await using var alias = RespireConnectionMultiplexer.Create(aliasEndpoint.Host, aliasEndpoint.Port);
        var index = new ClusterNodeIdentityIndex(preferredEndpoint, preferred, _ => alias);
        _ = index.GetOrCreate(aliasEndpoint);

        var resolved = index.ApplySnapshot([new(0, 3, preferredEndpoint, null, [aliasEndpoint])]);

        await Assert.That(ReferenceEquals(resolved[0].Node, preferred)).IsTrue();
        await Assert.That(ReferenceEquals(index.GetOrCreate(aliasEndpoint), preferred)).IsTrue();
        await Assert.That(ReferenceEquals(index.GetCurrent(alias), preferred)).IsTrue();
        await Assert.That(index.All).IsEquivalentTo([preferred, alias]);
    }
}
