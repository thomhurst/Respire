using System.Runtime.CompilerServices;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class AsyncFlushSignalTests
{
    private const int Wakes = 200;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentSignalsCoalesceWithoutLeavingAnExtraWake(bool preferInline)
    {
        var signal = new AsyncFlushSignal();
        signal.Signal(preferInline);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => signal.Signal(preferInline))));
        await signal.WaitAsync();

        var next = signal.WaitAsync();
        await Assert.That(next.IsCompleted).IsFalse();
        signal.Signal(preferInline);
        await next.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentPublicationAndRearmingDrainEveryPublishedItem(bool preferInline)
    {
        const int producers = 8;
        const int perProducer = 1_000;
        var signal = new AsyncFlushSignal();
        var gate = new object();
        var buffer = new Queue<int>();
        var observed = new bool[producers * perProducer];
        var drained = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = signal.WaitAsync();
        var writers = Enumerable.Range(0, producers).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                lock (gate) buffer.Enqueue(producer * perProducer + i);
                signal.Signal(preferInline);
                if (i % 16 == 0) Thread.Yield();
            }
        })).ToArray();
        // Signals coalesce; consumers inspect authoritative work after every wake.
        do
        {
            await pending.AsTask().WaitAsync(deadline.Token);
            lock (gate)
            {
                while (buffer.TryDequeue(out var item))
                {
                    if (observed[item]) throw new InvalidOperationException("A published item was drained twice.");
                    observed[item] = true;
                    drained++;
                }
            }
            if (drained == observed.Length) break;
            pending = signal.WaitAsync();
        } while (true);
        await Task.WhenAll(writers).WaitAsync(deadline.Token);
        await Assert.That(drained).IsEqualTo(producers * perProducer);
        await Assert.That(observed.All(item => item)).IsTrue();
    }

    [Test]
    public async Task DispatchedWakeResumesParkedWaiter()
    {
        var signal = new AsyncFlushSignal();
        var wait = signal.WaitAsync();
        await Assert.That(wait.IsCompleted).IsFalse();

        signal.Signal(preferInline: false);

        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // The wake consumed the signal, so the next wait parks again.
        await Assert.That(signal.WaitAsync().IsCompleted).IsFalse();
    }

    // A writer on a non-pool thread (a blocking caller, a benchmark harness) wakes the flush
    // loop through the thread pool on every command sent to an idle connection.
    [Test, NotInParallel]
    public async Task DispatchedWakeAllocatesNothing()
    {
        var waiter = new ParkedWaiter();
        for (var i = 0; i < 20; i++) { Measure(waiter, false); Measure(waiter, true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: Measure(waiter, false), Control: Measure(waiter, true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37L * Wakes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(ParkedWaiter waiter, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Wakes; i++)
        {
            waiter.ParkThenWake();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    /// <summary>Parks a continuation on the signal, as the flush loop does, and wakes it from the pool.</summary>
    private sealed class ParkedWaiter
    {
        private readonly AsyncFlushSignal _signal = new();
        private readonly Action _resume;
        private ValueTask _pending;
        private volatile bool _resumed;

        public ParkedWaiter() => _resume = Resume;

        public void ParkThenWake()
        {
            _resumed = false;
            _pending = _signal.WaitAsync();
            _pending.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_resume);
            _signal.Signal(preferInline: false);
            // Bounded so a lost wake fails this test instead of stalling the suite it runs alone in.
            var deadline = Environment.TickCount64 + 5_000;
            var spin = new SpinWait();
            while (!_resumed)
            {
                if (Environment.TickCount64 > deadline) throw new TimeoutException("The dispatched wake never resumed the waiter.");
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }

        private void Resume()
        {
            _pending.ConfigureAwait(false).GetAwaiter().GetResult();
            _resumed = true;
        }
    }
}
