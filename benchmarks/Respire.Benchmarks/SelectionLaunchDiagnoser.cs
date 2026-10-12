using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace Respire.Benchmarks;

/// <summary>Records each timed selection process, rather than a separate disassembly process.</summary>
internal sealed class SelectionLaunchDiagnoser(string directory, int launch, bool extraRun) : IDiagnoser
{
    private string _prefix = "";
    private int _pid;

    public IEnumerable<string> Ids => [nameof(SelectionLaunchDiagnoser)];
    public IEnumerable<IExporter> Exporters => [];
    public IEnumerable<IAnalyser> Analysers => [];
    public BenchmarkDotNet.Diagnosers.RunMode GetRunMode(BenchmarkCase benchmarkCase)
        => BenchmarkDotNet.Diagnosers.RunMode.None;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        if (signal == HostSignal.BeforeProcessStart)
        {
            Directory.CreateDirectory(directory);
            _prefix = Path.Combine(Path.GetFullPath(directory), Guid.NewGuid().ToString("N"));
            var environment = parameters.Process.StartInfo.Environment;
            environment["DOTNET_JitDisasm"] = "SelectConnection GetConnection GetSingleConnection *WorkloadAction*";
            environment["DOTNET_JitDisasmWithCodeBytes"] = "1";
            environment["DOTNET_JitDisasmWithAlignmentBoundaries"] = "1";
            environment["DOTNET_JitStdOutFile"] = _prefix + ".jit.txt";
            if (OperatingSystem.IsLinux()) environment["DOTNET_PerfMapEnabled"] = "3";
            return;
        }

        if (signal == HostSignal.AfterProcessStart)
        {
            _pid = parameters.Process.Id;
            return;
        }
        if (signal == HostSignal.AfterProcessExit)
        {
            if (OperatingSystem.IsLinux())
                File.Copy($"/tmp/perf-{_pid}.map", _prefix + ".perf.map");
            return;
        }

        if (signal is not (HostSignal.BeforeActualRun or HostSignal.AfterActualRun)) return;
        var process = parameters.Process;
        process.Refresh();
        var files = new Dictionary<string, string?>();
        if (OperatingSystem.IsLinux())
        {
            Capture(files, $"/proc/{process.Id}/status");
            Capture(files, $"/proc/{process.Id}/stat");
            foreach (var thread in Directory.EnumerateDirectories($"/proc/{process.Id}/task"))
            {
                Capture(files, thread + "/stat");
                Capture(files, thread + "/schedstat");
                Capture(files, thread + "/sched");
                Capture(files, thread + "/status");
            }
            foreach (var cpu in Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*")
                .Where(path => int.TryParse(Path.GetFileName(path).AsSpan(3), out _)))
            {
                Capture(files, cpu + "/cpufreq/scaling_cur_freq");
                Capture(files, cpu + "/cpufreq/scaling_governor");
            }
            Capture(files, "/proc/cpuinfo");
        }

        File.WriteAllText(_prefix + "." + signal + ".json", JsonSerializer.Serialize(new
        {
            schema = 1, signal = signal.ToString(), launch, pid = process.Id,
            platform = OperatingSystem.IsLinux() ? "linux" : "other",
            caseName = parameters.BenchmarkCase.DisplayInfo,
            parameters = parameters.BenchmarkCase.Parameters.DisplayInfo,
            revision = Environment.GetEnvironmentVariable("RESPIRE_BENCH_REVISION"),
            fixtureRevision = Environment.GetEnvironmentVariable("RESPIRE_BENCH_FIXTURE_REVISION"),
            phase = Environment.GetEnvironmentVariable("RESPIRE_BENCH_PHASE"),
            metricMode = Environment.GetEnvironmentVariable("RESPIRE_BENCH_ERRORS"),
            purpose = extraRun ? "disassembly" : Environment.GetEnvironmentVariable("RESPIRE_SELECTION_PURPOSE") ?? "public",
            job = parameters.BenchmarkCase.Job.ToString(),
            jobId = parameters.BenchmarkCase.Job.Id,
            environment = process.StartInfo.Environment.Where(pair =>
                pair.Key.StartsWith("DOTNET_", StringComparison.Ordinal) || pair.Key.StartsWith("COMPlus_", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value),
            utc = DateTimeOffset.UtcNow, timestamp = Stopwatch.GetTimestamp(),
            timestampFrequency = Stopwatch.Frequency,
            cpuSeconds = process.TotalProcessorTime.TotalSeconds,
            jitFile = Path.GetFileName(_prefix + ".jit.txt"), files,
            perfMap = OperatingSystem.IsLinux() ? Path.GetFileName(_prefix + ".perf.map") : null,
        }));
    }

    private static void Capture(Dictionary<string, string?> files, string path)
    {
        // A thread can exit between enumeration and reading. Keep absence visible.
        try { files[path] = File.ReadAllText(path); }
        catch (IOException error) { files[path] = "UNAVAILABLE: " + error.Message; }
        catch (UnauthorizedAccessException error) { files[path] = "UNAVAILABLE: " + error.Message; }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results) => [];
    public void DisplayResults(ILogger logger) { }
    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters) => [];
}
