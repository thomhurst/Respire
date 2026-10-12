using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Jobs;

var config = DefaultConfig.Instance
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

if (args is ["--transport-batch-diagnostics", var directory, var revision, var phase,
    var connections, var count, var launch, var iterations])
{
    await Respire.Benchmarks.TransportBatchDiagnostics.RunAsync(directory, revision, phase,
        int.Parse(connections), int.Parse(count), int.Parse(launch), int.Parse(iterations));
    return;
}

if (args is ["--transport-batch-trace-report", var trace, var report])
{
    Respire.Benchmarks.TransportBatchTraceReport.Write(trace, report);
    return;
}

// Short diagnostic jobs share a sampling profile. Change only one factor per control.
// They never replace the public Medium baseline/candidate/baseline comparison.
if (Environment.GetEnvironmentVariable("RESPIRE_SELECTION_CONTROLS") == "1")
{
    var reference = Job.ShortRun.WithLaunchCount(2);
    var affinity = long.Parse(Environment.GetEnvironmentVariable("RESPIRE_SELECTION_AFFINITY")
        ?? throw new ArgumentException("Selection controls require an allowed CPU affinity mask."),
        System.Globalization.CultureInfo.InvariantCulture);
    config = config.AddJob(reference.WithId("selection-reference"))
        .AddJob(reference.WithId("selection-tiering-off").WithEnvironmentVariable("DOTNET_TieredCompilation", "0"))
        .AddJob(reference.WithId("selection-pinned").WithAffinity(new IntPtr(affinity)))
        .AddJob(reference.WithId("selection-no-subtraction").WithEvaluateOverhead(false));
}

// Opt-in evidence runs keep every public benchmark body and its setup unchanged.
if (Environment.GetEnvironmentVariable("RESPIRE_SELECTION_EVIDENCE") is { Length: > 0 } evidence)
{
    var toolchain = Environment.Version.Major switch
    {
        8 => BenchmarkDotNet.Toolchains.CsProj.CsProjCoreToolchain.NetCoreApp80,
        10 => BenchmarkDotNet.Toolchains.CsProj.CsProjCoreToolchain.NetCoreApp10_0,
        _ => throw new ArgumentException("Selection evidence supports net8.0/net10.0 only."),
    };
    config = config.AddJob(Job.Default.WithToolchain(
        new Respire.Benchmarks.SelectionEvidenceToolchain(toolchain, evidence)).AsMutator());
}

// Discover parameter and argument combinations without running setup or measurements.
if (args is ["--transport-case-count", var destination, var preset])
{
    var job = preset switch
    {
        "Dry" => Job.Dry,
        "Medium" => Job.MediumRun,
        _ => throw new ArgumentException($"Unsupported transport acceptance job: {preset}"),
    };
    using var benchmarks = BenchmarkConverter.TypeToBenchmarks(
        typeof(Respire.Benchmarks.TransportAcceptanceBenchmarks), config.AddJob(job));
    File.WriteAllText(destination, benchmarks.BenchmarksCases.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
