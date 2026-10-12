using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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
    private const string AllocationProbeRuntime = "RESPIRE_TEST_FLUSH_ALLOCATION_RUNTIME";
    private const string ProbeReadyMarker = "RESPIRE_TEST_FLUSH_PROBE_READY";
    // Bounds runtime and coverage startup of a child on loaded CI workers.
    private static readonly TimeSpan ProbeStartupBudget = TimeSpan.FromSeconds(30);

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
        const int producers = 50;
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublicationWithoutWriteGateIsObservedAfterCoalescedSignals(bool preferInline)
    {
        const int publications = 10_000;
        var signal = new AsyncFlushSignal();
        var published = 0;
        var observed = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= publications; i++)
            {
                // Race the consumer's initial wake with publication and a second signal.
                // No write gate or caller-supplied full fence publishes this value.
                signal.Signal(preferInline);
                Volatile.Write(ref published, i);
                signal.Signal(preferInline);
                var spin = new SpinWait();
                while (Volatile.Read(ref observed) != i)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    spin.SpinOnce(sleep1Threshold: -1);
                }
            }
        });
        try
        {
            while (Volatile.Read(ref observed) < publications)
            {
                await signal.WaitAsync().AsTask().WaitAsync(deadline.Token);
                Volatile.Write(ref observed, Volatile.Read(ref published));
            }
            await writer.WaitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel();
            await writer;
        }
        await Assert.That(observed).IsEqualTo(publications);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentSignalsCoalesceWhileParkedWaiterIsResuming(bool preferInline)
    {
        const int signalers = 8;
        const int signalsPerThread = 25;
        var signal = new AsyncFlushSignal();
        var pending = signal.WaitAsync();
        var resumeCount = 0;
        var resumeThread = -1;
        var resumeOnPool = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The continuation records the wake and returns without consuming it (no GetResult), so
        // the waiter stays "resuming" — woken but with the signal unconsumed — while no thread
        // is held. Blocking the waking thread here instead starved the 1-CPU thread pool: the
        // blocked worker owned the queued `entered` continuation and the pool grew too slowly.
        pending.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            resumeThread = Environment.CurrentManagedThreadId;
            resumeOnPool = Thread.CurrentThread.IsThreadPoolThread;
            Interlocked.Increment(ref resumeCount);
            entered.TrySetResult();
        });
        // Dedicated threads keep the wake and the signal storm independent of pool capacity.
        var wakeThread = StartSignalThread(() => signal.Signal(preferInline));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (preferInline) await Assert.That(resumeThread).IsEqualTo(wakeThread.Thread.ManagedThreadId);
        else await Assert.That(resumeOnPool).IsTrue();

        using var start = new ManualResetEventSlim();
        var storm = Enumerable.Range(0, signalers).Select(_ => StartSignalThread(() =>
        {
            start.Wait();
            for (var i = 0; i < signalsPerThread; i++) signal.Signal(preferInline);
        })).ToArray();
        start.Set();
        await Task.WhenAll(storm.Select(s => s.Done).Append(wakeThread.Done)).WaitAsync(TimeSpan.FromSeconds(5));

        // Every signal raced the unconsumed wake: none may resume the waiter again or queue a
        // second wake (a second SetResult would throw on its pool thread).
        await Assert.That(Volatile.Read(ref resumeCount)).IsEqualTo(1);
        pending.ConfigureAwait(false).GetAwaiter().GetResult();

        var next = signal.WaitAsync();
        await Assert.That(next.IsCompleted).IsFalse();
        signal.Signal(preferInline);
        await next.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(Volatile.Read(ref resumeCount)).IsEqualTo(1);
    }

    private static (Thread Thread, Task Done) StartSignalThread(Action body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { body(); done.TrySetResult(); }
            catch (Exception error) { done.TrySetException(error); }
        }) { IsBackground = true, Name = "AsyncFlushSignalTests signaler" };
        thread.Start();
        return (thread, done.Task);
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
    // The measurement runs in a child process, which isolates its pool, GC and no-GC region.
    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DispatchedWakeAllocatesNothing(bool poolProducer)
    {
        // Pool growth allocates Thread/StartHelper on the signaling thread. Isolate capacity
        // from the test runner without changing its limits or subtracting runtime allocations.
        var start = CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(AsyncFlushSignalTests).Assembly.Location);
        start.Environment[AllocationProbeMode] = poolProducer ? "pool" : "dedicated";
        start.Environment[AllocationProbeRuntime] = Environment.Version.ToString();
        await RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    // A nonzero sample must explain itself: the report control injects one escaping allocation
    // at a known wake and checks that the failure names that wake, re-measures and lists runtime events.
    [Test, ParallelLimiter<ChildProcessProbes>]
    public async Task DispatchedWakeFailureReportsAllocatingWakeAndRuntimeEvents()
    {
        var start = CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(AsyncFlushSignalTests).Assembly.Location);
        start.Environment[AllocationProbeMode] = "report-control";
        start.Environment[AllocationProbeRuntime] = Environment.Version.ToString();
        var error = await Assert.That(() => RunProbeAsync(start, TimeSpan.FromSeconds(60)))
            .Throws<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("Expected zero wake allocations");
        await Assert.That(error.Message).Contains($"1 of {Wakes} wakes allocated");
        await Assert.That(error.Message).Contains($"wake {ReportControlWake}: +");
        await Assert.That(error.Message).Contains($"Re-measured sample {Remeasurements}/{Remeasurements} (not clean)");
        await Assert.That(error.Message).Contains("Runtime events in the failing interval:");
        await Assert.That(error.Message).Contains("(measuring) MethodJittingStarted");
        await Assert.That(error.Message).Contains($"MethodName={nameof(ReportControlAllocation)} ");
    }

    internal static ProcessStartInfo CreateProbeStartInfo(string? processPath, string? dotnetHostPath, string assemblyPath)
    {
        var processName = Path.GetFileName(processPath);
        // Unix apphosts have no executable suffix; dots in the assembly name are significant.
        if (processName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            processName = processName[..^4];
        var isAppHost = processName == Path.GetFileNameWithoutExtension(assemblyPath);
        var host = isAppHost || string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath : dotnetHostPath;
        if (string.IsNullOrEmpty(host) || !Path.IsPathFullyQualified(host))
            throw new InvalidOperationException("The probe needs the current apphost or an absolute DOTNET_HOST_PATH.");
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!isAppHost) start.ArgumentList.Add(assemblyPath);
        return start;
    }

    [Test]
    [Arguments("dotnet", false)]
    [Arguments("dotnet.exe", false)]
    [Arguments("Respire.Tests", true)]
    [Arguments("Respire.Tests.exe", true)]
    [Arguments("coverage-host", false)]
    public async Task ProbeUsesExplicitHostWithoutDependingOnPath(string executable, bool appHost)
    {
        var assembly = Path.GetFullPath("Respire.Tests.dll");
        var process = Path.GetFullPath(executable);
        var fallback = Path.GetFullPath("selected-host/dotnet");
        var start = CreateProbeStartInfo(process, fallback, assembly);
        await Assert.That(start.FileName).IsEqualTo(executable == "coverage-host" ? fallback : process);
        await Assert.That(start.ArgumentList.Count).IsEqualTo(appHost ? 0 : 1);
        if (!appHost) await Assert.That(start.ArgumentList[0]).IsEqualTo(assembly);
    }

    [Test]
    public async Task ProbeRejectsUnknownHostWithoutAnExplicitFallback()
    {
        await Assert.That(() => CreateProbeStartInfo(Path.GetFullPath("coverage-host"), null,
            Path.GetFullPath("Respire.Tests.dll"))).Throws<InvalidOperationException>();
    }

    [Test, ParallelLimiter<ChildProcessProbes>]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProbeFailuresIncludeBothOutputStreams(bool timeout)
    {
        var start = CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(AsyncFlushSignalTests).Assembly.Location);
        start.Environment[AllocationProbeMode] = timeout ? "timeout-control" : "failure-control";
        // The timeout control reports when both streams are written. Only then does its short
        // exit deadline start, so startup time on loaded CI workers cannot decide the outcome.
        var marker = timeout ? Path.Combine(Path.GetTempPath(), $"respire-probe-ready-{Guid.NewGuid():N}") : null;
        if (marker is not null) start.Environment[ProbeReadyMarker] = marker;
        Exception? error;
        try
        {
            error = await Assert.That(() => RunProbeAsync(start,
                    timeout ? TimeSpan.FromMilliseconds(250) : ProbeStartupBudget, marker))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            if (marker is not null) File.Delete(marker);
        }
        await Assert.That(error!.Message).Contains("probe control stdout");
        await Assert.That(error.Message).Contains("probe control stderr");
        await Assert.That(error.Message).Contains($"{Environment.NewLine}stderr: probe control stderr");
        if (timeout) await Assert.That(error.InnerException is TimeoutException).IsTrue();
        else await Assert.That(error.Message).Contains("code 17");
    }

    // readyMarker: a file the child creates once it is running. When given, the exit timeout
    // starts only after the marker exists (startup itself is bounded by ProbeStartupBudget).
    internal static async Task RunProbeAsync(ProcessStartInfo start, TimeSpan timeout, string? readyMarker = null)
    {
        using var process = Process.Start(start)!;
        using var readDeadline = new CancellationTokenSource();
        var output = process.StandardOutput.ReadToEndAsync(readDeadline.Token);
        var errors = process.StandardError.ReadToEndAsync(readDeadline.Token);
        Exception? failure = null;
        try
        {
            if (readyMarker is not null)
                await WaitForMarkerAsync(process, readyMarker).WaitAsync(ProbeStartupBudget);
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }

        // Always observe both readers, including after timeout/kill. Pipe cleanup has its own bound.
        readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await Task.WhenAll(output, errors); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        var diagnostics = $"stdout: {(output.IsCompletedSuccessfully ? output.Result : "<unavailable>")}"
            + $"{Environment.NewLine}stderr: {(errors.IsCompletedSuccessfully ? errors.Result : "<unavailable>")}";
        if (failure is not null) throw new InvalidOperationException($"Isolated probe failed. {diagnostics}", failure);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Isolated probe exited with code {process.ExitCode}. {diagnostics}");
    }

    private static async Task WaitForMarkerAsync(Process process, string marker)
    {
        while (!File.Exists(marker) && !process.HasExited) await Task.Delay(10);
    }

    internal static int? RunIsolatedAllocationProbe()
    {
        var mode = Environment.GetEnvironmentVariable(AllocationProbeMode);
        if (mode is "timeout-control" or "failure-control")
        {
            // Neither stream supplies a newline: the parent must separate their diagnostics.
            Console.Write("probe control stdout");
            Console.Error.Write("probe control stderr");
            if (Environment.GetEnvironmentVariable(ProbeReadyMarker) is { Length: > 0 } marker)
                File.WriteAllText(marker, "");
            if (mode == "timeout-control") Thread.Sleep(Timeout.Infinite);
            return 17;
        }
        if (mode is not ("pool" or "dedicated" or "report-control")) return null;
        try
        {
            var expectedRuntime = Environment.GetEnvironmentVariable(AllocationProbeRuntime);
            if (expectedRuntime != Environment.Version.ToString())
                throw new InvalidOperationException($"Probe runtime {Environment.Version} differs from parent {expectedRuntime}.");
            ThreadPool.GetMinThreads(out _, out var minIo);
            ThreadPool.GetMaxThreads(out _, out var maxIo);
            if (!ThreadPool.SetMinThreads(2, minIo) || !ThreadPool.SetMaxThreads(2, maxIo))
                throw new InvalidOperationException("Could not configure the isolated two-worker pool.");
            s_reportControl = mode == "report-control";

            // Started before the pool warms, so its dispatcher thread exists before measurement.
            using var events = new DispatchedWakeEventLog();

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

            MeasureSteadyState(events);
            if (mode == "pool") Task.Run(() => MeasureSteadyState(events)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    // After warmup, the report control injects one escaping allocation at this wake of the ordinary
    // path. Its first call JITs inside the measured interval, which the runtime event log must show.
    private const int ReportControlWake = 57;
    private const int Remeasurements = 8;
    private static bool s_reportControl;
    private static int s_allocateAtWake = -1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] ReportControlAllocation() => new byte[37];

    private static void MeasureSteadyState(DispatchedWakeEventLog events)
    {
        var waiter = new ParkedWaiter();
        var ordinary = new WakeTrace(Wakes);
        var control = new WakeTrace(Wakes);
        for (var i = 0; i < 20; i++) { Measure(waiter, false, ordinary); Measure(waiter, true, control); }
        if (s_reportControl) s_allocateAtWake = ReportControlWake;
        var measuringThread = events.MarkMeasuringThread();
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => MeasurePair(waiter, ordinary, control));
        Console.WriteLine($"poolProducer={Thread.CurrentThread.IsThreadPoolThread}; {measured}");
        if (!IsClean(measured))
            throw new InvalidOperationException($"Expected zero wake allocations, a positive allocation control, "
                + $"two existing workers and no collections; observed {measured}."
                + DescribeFailure(events, waiter, ordinary, control, measuringThread));
    }

    private static bool IsClean(WakeSample measured) =>
        measured.Bytes == 0 && measured.Control >= 37L * Wakes
        && measured.WorkersBefore == 2 && measured.WorkersAfter == 2
        && measured.Gen0 == 0 && measured.Gen1 == 0 && measured.Gen2 == 0;

    private static WakeSample MeasurePair(ParkedWaiter waiter, WakeTrace ordinary, WakeTrace control)
    {
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var workers = ThreadPool.ThreadCount;
        var completed = ThreadPool.CompletedWorkItemCount;
        var bytes = Measure(waiter, false, ordinary);
        var controlBytes = Measure(waiter, true, control);
        return new WakeSample(bytes, controlBytes, workers, ThreadPool.ThreadCount,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
            ThreadPool.CompletedWorkItemCount - completed, ThreadPool.PendingWorkItemCount);
    }

    // Runs only after a failed sample, which already decides the outcome. It reports where the
    // failing interval allocated, the runtime events around it, and whether fresh samples repeat it.
    private static string DescribeFailure(DispatchedWakeEventLog events, ParkedWaiter waiter,
        WakeTrace ordinary, WakeTrace control, long measuringThread)
    {
        var nl = Environment.NewLine;
        var report = new StringBuilder($"{nl}Failing interval on runtime {Environment.Version}, "
            + $"{RuntimeInformation.OSDescription}, {Environment.ProcessorCount} CPUs, "
            + $"managed thread {Environment.CurrentManagedThreadId}, pool thread {Thread.CurrentThread.IsThreadPoolThread}:");
        report.Append($"{nl}  ordinary: {ordinary.Describe()}{nl}  control: {control.Describe(maxWakes: 2)}");
        (string, DateTime)[] boundaries =
        [
            ("ordinary interval starts", ordinary.StartedUtc), ("ordinary interval ends", ordinary.EndedUtc),
            ("control interval starts", control.StartedUtc), ("control interval ends", control.EndedUtc),
        ];
        for (var sample = 1; sample <= Remeasurements; sample++)
        {
            // A diagnostic failure must not replace the original failing sample's message.
            try
            {
                var measured = AllocationMeasurement.WithoutConcurrentGc(() => MeasurePair(waiter, ordinary, control));
                report.Append($"{nl}Re-measured sample {sample}/{Remeasurements} ({(IsClean(measured) ? "clean" : "not clean")}): "
                    + $"{measured}{nl}  ordinary: {ordinary.Describe(maxWakes: 4)}");
            }
            catch (Exception error)
            {
                report.Append($"{nl}Re-measured sample {sample}/{Remeasurements} failed: {error.GetType().Name}: {error.Message}");
            }
        }
        events.WaitForDispatch();
        report.Append($"{nl}Runtime events in the failing interval: {events.Describe(measuringThread, 60, boundaries)}");
        return report.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(ParkedWaiter waiter, bool control, WakeTrace trace)
    {
        var allocated = trace.AllocatedAfterWake;
        var workers = trace.WorkersAfterWake;
        var allocateAt = s_allocateAtWake;
        trace.StartedUtc = DateTime.UtcNow;
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Wakes; i++)
        {
            waiter.ParkThenWake();
            if (control) GC.KeepAlive(new byte[37]);
            else if (i == allocateAt) GC.KeepAlive(ReportControlAllocation());
            // Per-wake readings go to preallocated arrays, so they add no bytes to the interval.
            allocated[i] = GC.GetAllocatedBytesForCurrentThread() - start;
            workers[i] = ThreadPool.ThreadCount;
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        trace.EndedUtc = DateTime.UtcNow;
        return bytes;
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
