using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Keva.Benchmarks;

var config = DefaultConfig.Instance
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
