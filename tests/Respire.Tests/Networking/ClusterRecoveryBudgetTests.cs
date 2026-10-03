using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterRecoveryBudgetTests
{
    [Test]
    public async Task TestClockTimersCanBeDisabledAndRearmedAfterFiring()
    {
        var clock = new RecoveryTestClock();
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(fired).IsEqualTo(0);
        timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(fired).IsEqualTo(1);
        timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(fired).IsEqualTo(2);
        timer.Dispose();
        await Assert.That(timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan)).IsFalse();
    }

    [Test]
    public async Task PrimaryExpiryReservesSeedTimeUntilOverallDeadline()
    {
        var clock = new RecoveryTestClock();
        using var budget = new ClusterRecoveryBudget(default, TimeSpan.FromSeconds(2), clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(budget.PrimaryToken.IsCancellationRequested).IsTrue();
        await Assert.That(budget.Token.IsCancellationRequested).IsFalse();
        var early = budget.GetFallbackToken(last: false);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        await Assert.That(early.IsCancellationRequested).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(early.IsCancellationRequested).IsTrue();
        await Assert.That(budget.GetFallbackToken(last: true).IsCancellationRequested).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(budget.Token.IsCancellationRequested).IsTrue();
        await Assert.That(budget.IsCallerCancellation(new OperationCanceledException(budget.Token), default)).IsFalse();
    }

    [Test]
    public async Task DelayedPrimaryContinuationCannotExtendOverallDeadline()
    {
        var clock = new RecoveryTestClock();
        using var budget = new ClusterRecoveryBudget(default, TimeSpan.FromSeconds(2), clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(budget.PrimaryToken.IsCancellationRequested).IsTrue();
        await Assert.That(budget.GetFallbackToken(last: false).IsCancellationRequested).IsTrue();
        await Assert.That(budget.GetFallbackToken(last: true).IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task LateSeedPhaseReservesHalfOfActualRemainingTime()
    {
        var clock = new RecoveryTestClock();
        using var budget = new ClusterRecoveryBudget(default, TimeSpan.FromSeconds(2), clock);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var early = budget.GetFallbackToken(last: false);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        await Assert.That(early.IsCancellationRequested).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(early.IsCancellationRequested).IsTrue();
        await Assert.That(budget.GetFallbackToken(last: true).IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task CallerCancellationReachesEveryPhase()
    {
        var clock = new RecoveryTestClock();
        using var caller = new CancellationTokenSource();
        using var budget = new ClusterRecoveryBudget(caller.Token, TimeSpan.FromSeconds(2), clock);
        var early = budget.GetFallbackToken(last: false);
        caller.Cancel();
        foreach (var token in new[] { budget.Token, budget.PrimaryToken, early })
        {
            await Assert.That(token.IsCancellationRequested).IsTrue();
            await Assert.That(budget.IsCallerCancellation(new OperationCanceledException(token), caller.Token)).IsTrue();
        }
    }
}
