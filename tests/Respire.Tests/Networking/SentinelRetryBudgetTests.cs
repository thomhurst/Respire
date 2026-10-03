using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelRetryBudgetTests
{
    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task MaximumCountsReplacementsAfterInitialAttempt(int maximum)
    {
        var budget = new SentinelRetryBudget(new() { MaxAttempts = maximum });
        await Assert.That(budget.Attempts).IsEqualTo(0);
        for (var attempt = 1; attempt <= maximum; attempt++)
        {
            await Assert.That(budget.IsExhausted).IsFalse();
            budget.StartRetry();
            await Assert.That(budget.Attempts).IsEqualTo(attempt);
        }
        await Assert.That(budget.IsExhausted).IsTrue();
        await Assert.That(() => budget.StartRetry()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(budget.Attempts).IsEqualTo(maximum);
    }

    [Test]
    public async Task DefaultBudgetHasUnlimitedCappedBackoff()
    {
        var budget = new SentinelRetryBudget(null);
        int[] seconds = [1, 2, 4, 8, 16, 30, 30, 30];
        foreach (var expected in seconds)
        {
            budget.StartRetry();
            await Assert.That(budget.GetDelay()).IsEqualTo(TimeSpan.FromSeconds(expected));
            await Assert.That(budget.IsExhausted).IsFalse();
        }
    }

    [Test]
    public async Task ExplicitUnlimitedPolicyUsesItsOwnDelay()
    {
        var budget = new SentinelRetryBudget(new()
        {
            InitialDelay = TimeSpan.FromMilliseconds(20), MaxDelay = TimeSpan.FromMilliseconds(50),
            BackoffMultiplier = 2, JitterRatio = 0,
        });
        int[] milliseconds = [20, 40, 50, 50];
        foreach (var expected in milliseconds)
        {
            budget.StartRetry();
            await Assert.That(budget.GetDelay()).IsEqualTo(TimeSpan.FromMilliseconds(expected));
        }
        budget.MarkSubscriptionExhausted();
        await Assert.That(budget.Attempts).IsEqualTo(4);
        await Assert.That(budget.IsExhausted).IsFalse();
    }

    [Test]
    public async Task SubscriptionExhaustionConsumesTheOuterBudgetUntilExplicitReset()
    {
        var budget = new SentinelRetryBudget(new() { MaxAttempts = 3, JitterRatio = 0 });
        budget.MarkSubscriptionExhausted();
        await Assert.That(budget.Attempts).IsEqualTo(3);
        await Assert.That(budget.IsExhausted).IsTrue();
        budget.Reset();
        await Assert.That(budget.Attempts).IsEqualTo(0);
        await Assert.That(budget.IsExhausted).IsFalse();
        budget.StartRetry();
        await Assert.That(budget.GetDelay()).IsEqualTo(TimeSpan.FromMilliseconds(250));
    }

    [Test]
    public async Task SuccessfulEpisodeResetRestoresTheFirstDelay()
    {
        var budget = new SentinelRetryBudget(null);
        budget.StartRetry();
        budget.StartRetry();
        budget.Reset();
        budget.StartRetry();
        await Assert.That(budget.Attempts).IsEqualTo(1);
        await Assert.That(budget.GetDelay()).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task ImmediatePendingWorkStillConsumesTheSameBudget()
    {
        var budget = new SentinelRetryBudget(new() { MaxAttempts = 2 });
        // Pending hints skip delay, but each failed discovery still spends one retry.
        budget.StartRetry();
        budget.StartRetry();
        await Assert.That(budget.IsExhausted).IsTrue();
        await Assert.That(budget.Attempts).IsEqualTo(2);
    }
}
