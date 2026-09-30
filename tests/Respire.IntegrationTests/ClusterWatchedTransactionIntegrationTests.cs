using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ClusterTransactionTestContainer>(Shared = SharedType.PerTestSession)]
public class ClusterWatchedTransactionIntegrationTests(ClusterTransactionTestContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task SameSlotWatchCommitsOrAbortsAfterAConflictingWrite(int protocol, bool conflict)
    {
        var options = new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], UseCluster = true, Connections = 2,
            Protocol = (RespProtocol)protocol,
        };
        await using var client = await RespireClient.ConnectAsync(options);
        await using var other = await RespireClient.ConnectAsync(options);
        var prefix = $"cluster-watch:{Guid.NewGuid():N}:";
        var view = client.WithKeyPrefix(prefix);
        var competitor = other.WithKeyPrefix(prefix);
        await view.SetAsync("{a}:balance", 100);
        await using var transaction = await view.CreateTransactionAsync(["{a}:balance", "{a}:history"]);
        (await view.GetAsync<long>("{a}:balance")).Should().Be(100);
        Action crossSlot = () => transaction.Set("{b}:bad", "wrong");
        crossSlot.Should().Throw<InvalidOperationException>();
        transaction.Count.Should().Be(0);
        var balance = transaction.Increment("{a}:balance");
        var history = transaction.Set("{a}:history", "committed");
        if (conflict) await competitor.SetAsync("{a}:balance", 99);
        (await transaction.CommitAsync()).Should().Be(!conflict);
        if (conflict)
        {
            balance.Status.Should().Be(RespirePendingStatus.Aborted);
            history.Status.Should().Be(RespirePendingStatus.Aborted);
            (await view.GetAsync<long>("{a}:balance")).Should().Be(99);
            (await view.GetStringAsync("{a}:history")).Should().BeNull();
        }
        else
        {
            balance.Result.Should().Be(101);
            history.Result.Should().BeTrue();
            (await view.GetAsync<long>("{a}:balance")).Should().Be(101);
            (await view.GetStringAsync("{a}:history")).Should().Be("committed");
        }
        // The dedicated lease is reusable after both a successful EXEC and a WATCH abort.
        await using var next = await view.CreateTransactionAsync(["{a}:balance"]);
        _ = next.Set("{a}:history", "fresh attempt");
        (await next.CommitAsync()).Should().BeTrue();
        await view.Keys.DeleteAsync("{a}:balance", "{a}:history");
    }
}
