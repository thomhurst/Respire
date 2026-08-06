using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Respire.StressTests.Clients;

namespace Respire.StressTests.Reporting;

/// <summary>
/// Builds the human-facing comparison report: a headline throughput table pairing the
/// two clients per scenario, then a full per-pass detail table. Written as GitHub
/// Flavored Markdown so the CI workflow can drop it straight into the job summary.
/// </summary>
internal static class MarkdownReporter
{
    public static string Build(IReadOnlyList<StressTestResult> results, StressTestOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Respire vs StackExchange.Redis — sustained stress");
        sb.AppendLine();
        sb.Append(Invariant($"{options.DurationSeconds / 60.0:0.#} min measured (+{options.WarmupSeconds}s warmup) per scenario/client pass, "));
        sb.Append(Invariant($"{options.Concurrency} concurrent workers, {options.ValueSizeBytes:N0} B values, "));
        sb.AppendLine(Invariant($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}."));
        sb.AppendLine();

        var scenarios = results.Select(r => r.Scenario).Distinct().ToList();

        AppendThroughputSummary(sb, results, scenarios);
        AppendDetails(sb, results);
        AppendFailures(sb, results);

        sb.AppendLine("## Notes");
        sb.AppendLine();
        sb.AppendLine("- Latency is per operation as issued by the workload; composite scenarios (hash, list) time the pair as one operation.");
        sb.AppendLine("- Alloc/op and CPU µs/op include a harness overhead that is identical for both clients.");
        sb.AppendLine("- Drift compares the last-third average of per-second throughput samples against the first third; a sustained negative value indicates degradation over the run.");

        return sb.ToString();
    }

    private static void AppendThroughputSummary(
        StringBuilder sb, IReadOnlyList<StressTestResult> results, IReadOnlyList<string> scenarios)
    {
        sb.AppendLine("## Throughput");
        sb.AppendLine();
        sb.AppendLine("| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |");
        sb.AppendLine("|---|---:|---:|---:|");

        foreach (var scenario in scenarios)
        {
            var baseline = FindResult(results, scenario, StackExchangeStressClient.ClientName);
            var respire = FindResult(results, scenario, RespireStressClient.ClientName);

            var ratio = baseline is { OperationsPerSecond: > 0 } && respire is not null
                ? Invariant($"{respire.OperationsPerSecond / baseline.OperationsPerSecond:0.00}x")
                : "—";

            sb.AppendLine(Invariant(
                $"| {scenario} | {FormatOps(baseline)} | {FormatOps(respire)} | {ratio} |"));
        }

        sb.AppendLine();
        sb.AppendLine("A ratio above 1.00x means Respire sustained more operations per second.");
        sb.AppendLine();
    }

    private static void AppendDetails(StringBuilder sb, IReadOnlyList<StressTestResult> results)
    {
        sb.AppendLine("## Details");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Client | Ops/s | p50 ms | p95 ms | p99 ms | p99.9 ms | Max ms | Errors | Alloc/op | Gen0/1/2 | GC pause s | CPU µs/op | Drift % | Status |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");

        foreach (var r in results)
        {
            sb.Append(Invariant($"| {r.Scenario} | {r.Client} | {r.OperationsPerSecond:N0} "));
            sb.Append(Invariant($"| {r.Latency.P50Ms:0.000} | {r.Latency.P95Ms:0.000} | {r.Latency.P99Ms:0.000} "));
            sb.Append(Invariant($"| {r.Latency.P999Ms:0.000} | {r.Latency.MaxMs:0.0} | {r.Errors:N0} "));
            sb.Append(Invariant($"| {FormatBytes(r.AllocatedBytesPerOperation)} "));
            sb.Append(Invariant($"| {r.Gc.Gen0Collections}/{r.Gc.Gen1Collections}/{r.Gc.Gen2Collections} "));
            sb.Append(Invariant($"| {r.Gc.PauseMs / 1000.0:0.00} "));
            sb.Append(Invariant($"| {(r.CpuMicrosecondsPerOperation is { } cpu ? cpu.ToString("0.0", CultureInfo.InvariantCulture) : "—")} "));
            sb.Append(Invariant($"| {(r.ThroughputDriftPercent is { } drift ? drift.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) : "—")} "));
            sb.AppendLine(Invariant($"| {r.Status} |"));
        }

        sb.AppendLine();
    }

    private static void AppendFailures(StringBuilder sb, IReadOnlyList<StressTestResult> results)
    {
        var failed = results.Where(r => r.Failed).ToList();
        if (failed.Count == 0)
            return;

        sb.AppendLine("## Failures");
        sb.AppendLine();
        foreach (var r in failed)
        {
            sb.AppendLine(Invariant($"- **{r.Scenario} / {r.Client}**: {r.Status}, {r.Errors:N0} errors"));
            foreach (var sample in r.ErrorSamples)
            {
                sb.AppendLine($"  - `{sample}`");
            }
        }

        sb.AppendLine();
    }

    private static StressTestResult? FindResult(
        IReadOnlyList<StressTestResult> results, string scenario, string client) =>
        results.FirstOrDefault(r => r.Scenario == scenario && r.Client == client);

    private static string FormatOps(StressTestResult? result) =>
        result is null ? "—" : Invariant($"{result.OperationsPerSecond:N0}");

    private static string FormatBytes(double? bytes) =>
        bytes switch
        {
            null => "—",
            >= 1024 * 1024 => Invariant($"{bytes / (1024.0 * 1024.0):0.00} MB"),
            >= 1024 => Invariant($"{bytes / 1024.0:0.00} KB"),
            _ => Invariant($"{bytes:0} B")
        };

    private static string Invariant(FormattableString formattable) =>
        FormattableString.Invariant(formattable);
}
