using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterRecoveryBudgetTests
{
    [Test]
    public async Task PrimaryExpiryReservesSeedTimeUntilOverallDeadline()
    {
        var clock = new RecoveryTestClock();
        using var budget = new ClusterRecoveryBudget(default, TimeSpan.FromSeconds(2), clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(budget.PrimaryToken.IsCancellationRequested).IsTrue();
        await Assert.That(budget.Token.IsCancellationRequested).IsFalse();
        var early = budget.GetFallbackToken(last: false);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(early.IsCancellationRequested).IsTrue();
        await Assert.That(budget.GetFallbackToken(last: true).IsCancellationRequested).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(budget.Token.IsCancellationRequested).IsTrue();
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

/// <summary>Advances deadlines only when the test has observed the relevant network operation.</summary>
internal sealed class RecoveryTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<RecoveryTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new RecoveryTimer(this, callback, state, _ticks + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        RecoveryTimer[] due;
        lock (_gate)
        {
            _ticks += elapsed.Ticks;
            due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class RecoveryTimer(RecoveryTestClock clock, TimerCallback callback, object? state, long due) : ITimer
    {
        private int _disposed;
        internal long Due { get; private set; } = due;
        internal void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                Due = clock._ticks + dueTime.Ticks;
                return true;
            }
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            lock (clock._gate) clock._timers.Remove(this);
        }
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
