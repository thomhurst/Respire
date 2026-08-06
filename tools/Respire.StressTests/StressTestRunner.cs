using System.Diagnostics;
using Respire.StressTests.Clients;
using Respire.StressTests.Metrics;
using Respire.StressTests.Reporting;
using Respire.StressTests.Scenarios;

namespace Respire.StressTests;

/// <summary>
/// Runs one scenario against one client: seeds keys, warms up, then drives the
/// configured number of concurrent workers for the sustained measured window while
/// sampling throughput once per second for drift analysis. A watchdog aborts the
/// pass if no operation completes for a full minute, and an error storm aborts it
/// early instead of burning the remaining window.
/// </summary>
internal static class StressTestRunner
{
    private const int MaxErrorsBeforeAbort = 1000;
    private const int StallSecondsBeforeAbort = 60;
    private const int ProgressReportSeconds = 30;

    public static async Task<StressTestResult> RunAsync(
        StressScenario scenario,
        IStressClient client,
        StressTestOptions options)
    {
        // Client name in the prefix keeps every pass in its own key space, so INCR
        // counters and list keys never carry state across passes.
        var keyPrefix = $"stress:{scenario.Name}:{client.Name}";
        var payload = new string('X', options.ValueSizeBytes);

        var contexts = new WorkerContext[options.Concurrency];
        for (var i = 0; i < contexts.Length; i++)
        {
            contexts[i] = new WorkerContext
            {
                WorkerIndex = i,
                Payload = payload,
                SeededStringKey = $"{keyPrefix}:seeded:string",
                SeededHashKey = $"{keyPrefix}:seeded:hash",
                WorkerStringKey = $"{keyPrefix}:string:{i}",
                WorkerCounterKey = $"{keyPrefix}:counter:{i}",
                WorkerHashKey = $"{keyPrefix}:hash:{i}",
                WorkerListKey = $"{keyPrefix}:list:{i}"
            };
        }

        await client.SetStringAsync(contexts[0].SeededStringKey, payload).ConfigureAwait(false);
        await client.HashSetAsync(contexts[0].SeededHashKey, "field", payload).ConfigureAwait(false);

        // Warmup: same workload, nothing recorded. Errors here indicate a broken
        // setup rather than a runtime failure, so they propagate and fail the pass.
        using (var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.WarmupSeconds)))
        {
            await RunWorkersAsync(scenario, client, contexts, latency: null, tracker: null, warmupCts.Token)
                .ConfigureAwait(false);
        }

        var latency = new LatencyTracker();
        var tracker = new ThroughputTracker();
        var gcStats = new GcStats();
        var startedAtUtc = DateTime.UtcNow;

        using var stopCts = new CancellationTokenSource();
        tracker.Start();
        var workers = RunWorkersAsync(scenario, client, contexts, latency, tracker, stopCts.Token);

        var stalled = await SampleUntilDoneAsync(scenario, client, tracker, options, workers).ConfigureAwait(false);

        // Workers exit at the next loop check; in-flight operations complete normally
        // rather than being cancelled mid-send.
        stopCts.Cancel();
        await workers.ConfigureAwait(false);
        tracker.Stop();

        return new StressTestResult
        {
            Scenario = scenario.Name,
            ScenarioDescription = scenario.Description,
            Client = client.Name,
            DurationSeconds = options.DurationSeconds,
            WarmupSeconds = options.WarmupSeconds,
            Concurrency = options.Concurrency,
            ValueSizeBytes = options.ValueSizeBytes,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = tracker.Elapsed.TotalSeconds,
            Operations = tracker.OperationCount,
            Errors = tracker.ErrorCount,
            ErrorSamples = tracker.GetErrorSamples(),
            OperationsPerSecondSamples = tracker.GetSamples(),
            Latency = latency.ToSnapshot(),
            Gc = gcStats.Capture(),
            CpuTimeSeconds = tracker.CpuTimeSeconds,
            Stalled = stalled
        };
    }

    /// <summary>
    /// Drives the one-second sampling loop for the measured window. Returns true if
    /// the pass stalled (no operations completed for <see cref="StallSecondsBeforeAbort"/>
    /// consecutive seconds).
    /// </summary>
    private static async Task<bool> SampleUntilDoneAsync(
        StressScenario scenario,
        IStressClient client,
        ThroughputTracker tracker,
        StressTestOptions options,
        Task workers)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(options.DurationSeconds * (double)Stopwatch.Frequency);
        long lastOperations = 0;
        var stallSamples = 0;
        var secondsElapsed = 0;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            // If every worker died (warmup-style fatal fault), stop sampling early.
            var completed = await Task.WhenAny(workers, Task.Delay(1000)).ConfigureAwait(false);
            if (completed == workers)
                break;

            tracker.SampleInterval();
            secondsElapsed++;

            var operations = tracker.OperationCount;
            if (operations == lastOperations)
            {
                if (++stallSamples >= StallSecondsBeforeAbort)
                {
                    Console.WriteLine(
                        $"  [{scenario.Name}/{client.Name}] STALL: no operations completed for {StallSecondsBeforeAbort}s, aborting pass");
                    return true;
                }
            }
            else
            {
                stallSamples = 0;
                lastOperations = operations;
            }

            if (tracker.ErrorCount >= MaxErrorsBeforeAbort)
            {
                Console.WriteLine(
                    $"  [{scenario.Name}/{client.Name}] ERROR STORM: {tracker.ErrorCount} errors, aborting pass");
                return false;
            }

            if (secondsElapsed % ProgressReportSeconds == 0)
            {
                var rate = tracker.Elapsed.TotalSeconds > 0 ? operations / tracker.Elapsed.TotalSeconds : 0;
                Console.WriteLine(
                    $"  [{scenario.Name}/{client.Name}] {secondsElapsed}s: {operations:N0} ops ({rate:N0} ops/s avg, {tracker.ErrorCount:N0} errors)");
            }
        }

        return false;
    }

    private static Task RunWorkersAsync(
        StressScenario scenario,
        IStressClient client,
        WorkerContext[] contexts,
        LatencyTracker? latency,
        ThroughputTracker? tracker,
        CancellationToken stopToken)
    {
        var tasks = new Task[contexts.Length];
        for (var i = 0; i < contexts.Length; i++)
        {
            var context = contexts[i];
            tasks[i] = Task.Run(
                () => WorkerLoopAsync(scenario, client, context, latency, tracker, stopToken),
                CancellationToken.None);
        }

        return Task.WhenAll(tasks);
    }

    private static async Task WorkerLoopAsync(
        StressScenario scenario,
        IStressClient client,
        WorkerContext context,
        LatencyTracker? latency,
        ThroughputTracker? tracker,
        CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                await scenario.Execute(client, context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (tracker is null)
                    throw; // warmup: fail the pass immediately

                tracker.RecordError(ex);
                if (tracker.ErrorCount >= MaxErrorsBeforeAbort)
                    return;

                continue;
            }

            tracker?.RecordOperation();
            latency?.RecordTicks(Stopwatch.GetTimestamp() - start);
        }
    }
}
