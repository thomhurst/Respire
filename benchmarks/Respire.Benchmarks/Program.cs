using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Jobs;

var config = DefaultConfig.Instance
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

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
