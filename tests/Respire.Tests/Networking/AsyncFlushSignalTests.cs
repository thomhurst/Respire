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
    private const string AllocationProbeRuntime = "RESPIRE_TEST_FLUSH_ALLOCATION_RUNTIME";

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
        var start = CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(AsyncFlushSignalTests).Assembly.Location);
        start.Environment[AllocationProbeMode] = poolProducer ? "pool" : "dedicated";
        start.Environment[AllocationProbeRuntime] = Environment.Version.ToString();
        await RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    private static ProcessStartInfo CreateProbeStartInfo(string? processPath, string? dotnetHostPath, string assemblyPath)
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

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProbeFailuresIncludeBothOutputStreams(bool timeout)
    {
        var start = CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(AsyncFlushSignalTests).Assembly.Location);
        start.Environment[AllocationProbeMode] = timeout ? "timeout-control" : "failure-control";
        // Leave room for runtime and coverage startup on loaded CI workers.
        var error = await Assert.That(() => RunProbeAsync(start, TimeSpan.FromSeconds(30)))
            .Throws<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("probe control stdout");
        await Assert.That(error.Message).Contains("probe control stderr");
        await Assert.That(error.Message).Contains($"{Environment.NewLine}stderr: probe control stderr");
        if (timeout) await Assert.That(error.InnerException is TimeoutException).IsTrue();
        else await Assert.That(error.Message).Contains("code 17");
    }

    private static async Task RunProbeAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start)!;
        using var readDeadline = new CancellationTokenSource();
        var output = process.StandardOutput.ReadToEndAsync(readDeadline.Token);
        var errors = process.StandardError.ReadToEndAsync(readDeadline.Token);
        Exception? failure = null;
        try
        {
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
        if (failure is not null) throw new InvalidOperationException($"Allocation probe failed. {diagnostics}", failure);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Allocation probe exited with code {process.ExitCode}. {diagnostics}");
    }

    internal static int? RunIsolatedAllocationProbe()
    {
        var mode = Environment.GetEnvironmentVariable(AllocationProbeMode);
        if (mode is "timeout-control" or "failure-control")
        {
            // Neither stream supplies a newline: the parent must separate their diagnostics.
            Console.Write("probe control stdout");
            Console.Error.Write("probe control stderr");
            if (mode == "timeout-control") Thread.Sleep(Timeout.Infinite);
            return 17;
        }
        if (mode is not ("pool" or "dedicated")) return null;
        try
        {
            var expectedRuntime = Environment.GetEnvironmentVariable(AllocationProbeRuntime);
            if (expectedRuntime != Environment.Version.ToString())
                throw new InvalidOperationException($"Probe runtime {Environment.Version} differs from parent {expectedRuntime}.");
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

            MeasureSteadyState();
            if (mode == "pool") Task.Run(MeasureSteadyState).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
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
