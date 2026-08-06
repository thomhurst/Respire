using System.Collections.Concurrent;
using System.Text.Json;
using Respire.StressTests.Clients;
using Respire.StressTests.Infrastructure;
using Respire.StressTests.Reporting;
using Respire.StressTests.Scenarios;

namespace Respire.StressTests;

/// <summary>
/// Respire Stress Test Runner — sustained throughput and latency comparison between
/// Respire and StackExchange.Redis across common Redis operations. Each scenario runs
/// for a sustained window per client so degradation over time (leaks, drift, stalls)
/// is visible where a short benchmark would miss it. See <see cref="StressTestOptions.Usage"/>.
/// </summary>
public static class Program
{
    private static readonly ConcurrentQueue<Exception> UnobservedTaskExceptions = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        // A task exception nobody awaited means a background failure escaped every
        // error path — collected here and escalated to a run failure at the end.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            UnobservedTaskExceptions.Enqueue(e.Exception);
            e.SetObserved();
        };

        StressTestOptions options;
        try
        {
            options = StressTestOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }

        try
        {
            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(StressTestOptions options)
    {
        var scenarios = StressScenarios.Select(options.Scenario);
        var clientNames = SelectClients(options.Client);

        Console.WriteLine("Respire Stress Test Runner");
        Console.WriteLine($"Duration: {options.DurationSeconds}s measured + {options.WarmupSeconds}s warmup per pass");
        Console.WriteLine($"Scenarios: {string.Join(", ", scenarios.Select(s => s.Name))}");
        Console.WriteLine($"Clients: {string.Join(", ", clientNames)}");
        Console.WriteLine($"Concurrency: {options.Concurrency} workers");
        Console.WriteLine($"Value size: {options.ValueSizeBytes:N0} B");
        Console.WriteLine(new string('-', 50));

        await using var redis = await RedisEndpoint.CreateAsync().ConfigureAwait(false);
        Console.WriteLine($"Redis endpoint: {redis.Host}:{redis.Port}");

        Directory.CreateDirectory(options.OutputPath);
        var results = new List<StressTestResult>();

        foreach (var scenario in scenarios)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {scenario.Name}: {scenario.Description} ===");

            foreach (var clientName in clientNames)
            {
                // Fresh client per pass so connection state and allocations never
                // leak between passes, and disposal is itself exercised every time.
                await using var client = await ConnectAsync(clientName, redis).ConfigureAwait(false);

                var result = await StressTestRunner.RunAsync(scenario, client, options).ConfigureAwait(false);
                results.Add(result);

                var fileName = $"{result.Scenario}-{SanitizeFileName(result.Client)}.json";
                await File.WriteAllTextAsync(
                    Path.Combine(options.OutputPath, fileName),
                    JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);

                Console.WriteLine(
                    $"  [{result.Scenario}/{result.Client}] done: {result.Operations:N0} ops, " +
                    $"{result.OperationsPerSecond:N0} ops/s, p50 {result.Latency.P50Ms:0.000} ms, " +
                    $"p99 {result.Latency.P99Ms:0.000} ms, {result.Errors:N0} errors — {result.Status}");
            }
        }

        var report = MarkdownReporter.Build(results, options);
        var reportPath = Path.Combine(options.OutputPath, "stress-report.md");
        await File.WriteAllTextAsync(reportPath, report).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(new string('-', 50));
        Console.WriteLine(report);
        Console.WriteLine($"Report written to {reportPath}");

        return CompleteRun(results);
    }

    private static int CompleteRun(List<StressTestResult> results)
    {
        // Give lingering background tasks a chance to fault, then surface anything
        // that finalized with an unobserved exception.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var exitCode = 0;

        var failed = results.Where(r => r.Failed).ToList();
        if (failed.Count > 0)
        {
            Console.WriteLine($"FAILED: {failed.Count} pass(es) had errors or stalled:");
            foreach (var result in failed)
            {
                Console.WriteLine($"  - {result.Scenario}/{result.Client}: {result.Status} ({result.Errors:N0} errors)");
            }

            exitCode = 1;
        }

        if (!UnobservedTaskExceptions.IsEmpty)
        {
            Console.WriteLine($"FAILED: {UnobservedTaskExceptions.Count} unobserved task exception(s):");
            foreach (var exception in UnobservedTaskExceptions.Take(5))
            {
                Console.WriteLine($"  - {exception.GetBaseException()}");
            }

            exitCode = 1;
        }

        if (exitCode == 0)
        {
            Console.WriteLine("All passes completed without errors.");
        }

        return exitCode;
    }

    private static IReadOnlyList<string> SelectClients(string client) =>
        client.ToLowerInvariant() switch
        {
            // Baseline always runs first within each scenario so both clients see the
            // runner in the same lifecycle phase scenario by scenario.
            "all" => [StackExchangeStressClient.ClientName, RespireStressClient.ClientName],
            "respire" => [RespireStressClient.ClientName],
            "stackexchange" => [StackExchangeStressClient.ClientName],
            _ => throw new ArgumentException(
                $"Unknown client '{client}'. Valid clients: respire, stackexchange, all")
        };

    private static async Task<IStressClient> ConnectAsync(string clientName, RedisEndpoint redis) =>
        clientName switch
        {
            RespireStressClient.ClientName =>
                await RespireStressClient.ConnectAsync(redis.Host, redis.Port).ConfigureAwait(false),
            StackExchangeStressClient.ClientName =>
                await StackExchangeStressClient.ConnectAsync(redis.Host, redis.Port).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(clientName), clientName, null)
        };

    private static string SanitizeFileName(string clientName) =>
        clientName.ToLowerInvariant().Replace('.', '-');
}
