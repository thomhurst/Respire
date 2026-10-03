using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterRecoveryBudgetTests
{
    [Test]
    public async Task PrimaryAndEarlySeedDeadlinesLeaveTheFinalSeedItsReservedTime()
    {
        var clock = new ClusterRecoveryTestClock();
        using var budget = new ClusterRecoveryBudget(default, TimeSpan.FromSeconds(2), clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(budget.PrimaryToken.IsCancellationRequested).IsTrue();
        await Assert.That(budget.Token.IsCancellationRequested).IsFalse();
        var earlySeed = budget.GetFallbackToken(last: false);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        await Assert.That(earlySeed.IsCancellationRequested).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(earlySeed.IsCancellationRequested).IsTrue();
        await Assert.That(budget.GetFallbackToken(last: true).IsCancellationRequested).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(budget.Token.IsCancellationRequested).IsTrue();
        await Assert.That(budget.IsCallerCancellation(new OperationCanceledException(budget.Token), default)).IsFalse();
    }

    [Test]
    public async Task DelayedContinuationCannotExtendTheOverallDeadline()
    {
        var clock = new ClusterRecoveryTestClock();
        using var budget = new ClusterRecoveryBudget(default, TimeSpan.FromSeconds(2), clock);
        // Reproduce the loaded-worker ordering: both timers expire before recovery resumes.
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(budget.PrimaryToken.IsCancellationRequested).IsTrue();
        await Assert.That(budget.GetFallbackToken(last: false).IsCancellationRequested).IsTrue();
        await Assert.That(budget.GetFallbackToken(last: true).IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task CallerCancellationReachesEveryPhaseWithoutAdvancingTime()
    {
        var clock = new ClusterRecoveryTestClock();
        using var caller = new CancellationTokenSource();
        using var budget = new ClusterRecoveryBudget(caller.Token, TimeSpan.FromSeconds(2), clock);
        var earlySeed = budget.GetFallbackToken(last: false);
        caller.Cancel();
        foreach (var token in new[] { budget.Token, budget.PrimaryToken, earlySeed })
        {
            await Assert.That(token.IsCancellationRequested).IsTrue();
            await Assert.That(budget.IsCallerCancellation(new OperationCanceledException(token), caller.Token)).IsTrue();
        }
    }
}
