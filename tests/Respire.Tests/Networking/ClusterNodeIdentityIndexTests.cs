using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterNodeIdentityIndexTests
{
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
