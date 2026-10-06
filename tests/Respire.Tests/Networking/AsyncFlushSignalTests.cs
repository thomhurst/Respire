using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[Category(TestCategories.ConstrainedRetirement)]
public class AsyncFlushSignalTests
{
    private const int Wakes = 200;
    private const string AllocationProbeMode = "RESPIRE_TEST_FLUSH_ALLOCATION_PROBE";

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

    // Check both global (non-pool producer) and local (pool producer) dispatch queues.
    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DispatchedWakeAllocatesNothing(bool poolProducer)
    {
        // Pool growth allocates Thread/StartHelper on the signaling thread. Isolate capacity
        // from the test runner without changing its limits or subtracting runtime allocations.
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(AsyncFlushSignalTests).Assembly.Location);
        start.Environment[AllocationProbeMode] = poolProducer ? "pool" : "dedicated";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(process.ExitCode).IsEqualTo(0).Because(await output + await errors);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [ModuleInitializer]
    internal static void RunIsolatedAllocationProbe()
    {
        var mode = Environment.GetEnvironmentVariable(AllocationProbeMode);
        if (mode is not ("pool" or "dedicated")) return;
        try
        {
            ThreadPool.GetMinThreads(out _, out var minIo);
            ThreadPool.GetMaxThreads(out _, out var maxIo);
            if (!ThreadPool.SetMinThreads(2, minIo) || !ThreadPool.SetMaxThreads(2, maxIo))
                throw new InvalidOperationException("Could not configure the isolated two-worker pool.");

            // Occupy both workers together so capacity is established before any measurement.
            using var entered = new CountdownEvent(2);
            using var release = new ManualResetEventSlim();
            using var finished = new CountdownEvent(2);
            for (var i = 0; i < 2; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    entered.Signal();
                    release.Wait(TimeSpan.FromSeconds(10));
                    finished.Signal();
                }, 0, preferLocal: false);
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Workers did not start.");
            }
            finally
            {
                release.Set();
                if (!finished.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Workers did not finish warming.");
            }

            // Complete module-owned type/delegate initialization on the initializer thread
            // before a worker enters this code; otherwise it can wait for this initializer.
            MeasureSteadyState();
            if (mode == "pool") Task.Run(MeasureSteadyState).GetAwaiter().GetResult();
            Environment.Exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }
    }

    private static void MeasureSteadyState()
    {
        var waiter = new ParkedWaiter();
        for (var i = 0; i < 20; i++) { Measure(waiter, false); Measure(waiter, true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
        {
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var workers = ThreadPool.ThreadCount;
            var bytes = Measure(waiter, false);
            var control = Measure(waiter, true);
            return (Bytes: bytes, Control: control, WorkersBefore: workers, WorkersAfter: ThreadPool.ThreadCount,
                Gen0: GC.CollectionCount(0) - gen0, Gen1: GC.CollectionCount(1) - gen1, Gen2: GC.CollectionCount(2) - gen2);
        });
        Console.WriteLine($"poolProducer={Thread.CurrentThread.IsThreadPoolThread}; {measured}");
        if (measured.Bytes != 0 || measured.Control < 37L * Wakes
            || measured.WorkersBefore != 2 || measured.WorkersAfter != 2
            || measured.Gen0 != 0 || measured.Gen1 != 0 || measured.Gen2 != 0)
            throw new InvalidOperationException($"Expected zero wake allocations, a positive allocation control, "
                + $"two existing workers and no collections; observed {measured}.");
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
