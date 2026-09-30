using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ThreadPoolMonitorTests
{
    private const string ChildMode = "RESPIRE_TEST_THREAD_POOL_PROBE";

    [Test]
    public async Task StarvationIsObservedBeforeTheBlockedWorkerIsReleased()
    {
        // Thread-pool limits are process-wide. Never starve the parallel test runner.
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(ThreadPoolMonitorTests).Assembly.Location);
        start.Environment[ChildMode] = "starvation";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.That(process.ExitCode).IsEqualTo(0).Because(await output + await errors);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Test]
    public async Task OptionsKeepMonitoringEnabledAndRejectInvalidThresholds()
    {
        var options = new RespireOptions { Endpoints = [new("localhost")] };
        await Assert.That(options.ThreadPoolMonitoring).IsTrue();
        await Assert.That(options.ThreadPoolWarningThreshold).IsEqualTo(TimeSpan.FromMilliseconds(500));
        foreach (var threshold in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1) })
            await Assert.That(() => RespireClient.Create(options with { ThreadPoolWarningThreshold = threshold }))
                .ThrowsExactly<RespireConfigurationException>();
    }

    [ModuleInitializer]
    internal static void RunIsolatedProbe()
    {
        if (Environment.GetEnvironmentVariable(ChildMode) != "starvation") return;
        try { RunStarvation(); Environment.Exit(0); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }
    }

    private static void RunStarvation()
    {
        ThreadPool.GetMinThreads(out _, out var minIo);
        ThreadPool.GetMaxThreads(out _, out var maxIo);
        Require(ThreadPool.SetMinThreads(1, minIo), "Could not set minimum workers.");
        Require(ThreadPool.SetMaxThreads(1, maxIo), "Could not set maximum workers.");
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ThreadPool.UnsafeQueueUserWorkItem(_ => { blocked.Set(); release.Wait(TimeSpan.FromSeconds(15)); }, 0, preferLocal: false);
        Require(blocked.Wait(TimeSpan.FromSeconds(5)), "Worker did not enter the blocking operation.");
        var ambient = new AsyncLocal<string?>();
        using var logger = new ProbeLogger(ambient);
        using var throwingLogger = new ProbeLogger(ambient, throwOnWarning: true);
        var measurements = new Dictionary<string, double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name.StartsWith("respire.thread_pool.", StringComparison.Ordinal))
                    owner.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => measurements[instrument.Name] = value);
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurements[instrument.Name] = value);
        listener.Start();
        var options = new RespireOptions
        {
            Endpoints = [new("localhost", 1)], LoggerFactory = logger,
            ThreadPoolWarningThreshold = TimeSpan.FromMilliseconds(50),
        };
        var disabled = RespireClient.Create(options with { ThreadPoolMonitoring = false });
        Require(ThreadPoolMonitor.Latest is null, "Opted-out client started the probe.");
        disabled.DisposeAsync().GetAwaiter().GetResult();
        ambient.Value = "request context";
        var throwingClient = RespireClient.Create(options with { LoggerFactory = throwingLogger });
        var client = RespireClient.Create(options);
        try
        {
            Require(logger.Warning.Wait(TimeSpan.FromSeconds(5)), "No starvation warning while the worker was blocked.");
            var snapshot = RespireTimeoutDiagnostics.Capture().ThreadPoolProbe;
            Require(snapshot is { IsPending: true }, "Timeout snapshot did not retain the pending probe.");
            Require(snapshot!.SchedulingDelay >= TimeSpan.FromMilliseconds(50), "Scheduling delay was not measured.");
            Require(snapshot.BusyWorkerThreads >= 1 && snapshot.MinWorkerThreads == 1 && snapshot.PendingWorkItems >= 1,
                "Thread-pool counters did not describe the blocked worker.");
            Require(logger.Message.Contains("blocking", StringComparison.OrdinalIgnoreCase), "Warning lacks remediation guidance.");
            Require(logger.ObservedContext is null, "Shared monitor retained the creating request's execution context.");
            Require(throwingLogger.Count == 1, "Throwing observer prevented the other warning.");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            Require(logger.Count == 1, "Warning was not throttled.");
            Require(ThreadPool.PendingWorkItemCount == 1, "Starvation accumulated probes or clients started separate probes.");
            listener.RecordObservableInstruments();
            Require(measurements["respire.thread_pool.scheduling.delay"] >= .05, "Missing delay metric.");
            Require(measurements["respire.thread_pool.workers.busy"] >= 1, "Missing busy-worker metric.");
            Require(measurements["respire.thread_pool.workers.min"] == 1, "Missing minimum-worker metric.");
            Require(measurements["respire.thread_pool.work.pending"] >= 1, "Missing pending-work metric.");
            // Disposal must not wait for a starved callback to run.
            client.WithKeyPrefix("view:").DisposeAsync().GetAwaiter().GetResult();
            Require(ThreadPoolMonitor.Latest is not null, "Disposing a view stopped its root's probe.");
            client.DisposeAsync().GetAwaiter().GetResult();
            Require(ThreadPoolMonitor.Latest is not null, "Disposing one client stopped another client's probe.");
            throwingClient.DisposeAsync().GetAwaiter().GetResult();
            Require(ThreadPoolMonitor.Latest is null, "Last disposal retained an active sample.");
            Require(snapshot.IsPending, "Published timeout snapshot was mutated during disposal.");
            RespireClient restarted;
            using (ExecutionContext.SuppressFlow()) restarted = RespireClient.Create(options);
            try
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
                Require(ThreadPool.PendingWorkItemCount == 1, "Restarting the monitor accumulated abandoned probes.");
                release.Set();
                Require(SpinWait.SpinUntil(() => ThreadPoolMonitor.Latest is { IsPending: false }, TimeSpan.FromSeconds(5)),
                    "The restarted monitor did not observe pool recovery.");
                Require(snapshot.IsPending, "Pool recovery mutated a retained timeout snapshot.");
            }
            finally { restarted.DisposeAsync().GetAwaiter().GetResult(); }
        }
        finally
        {
            release.Set();
            client.DisposeAsync().GetAwaiter().GetResult();
            throwingClient.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbeLogger(AsyncLocal<string?> ambient, bool throwOnWarning = false) : ILoggerFactory, ILogger
    {
        internal readonly ManualResetEventSlim Warning = new();
        internal string Message = "";
        internal string? ObservedContext;
        internal int Count;
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() => Warning.Dispose();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level != LogLevel.Warning) return;
            Interlocked.Increment(ref Count);
            if (throwOnWarning) throw new InvalidOperationException("Observer failure.");
            ObservedContext = ambient.Value;
            Message = formatter(state, exception);
            Warning.Set();
        }
    }
}
