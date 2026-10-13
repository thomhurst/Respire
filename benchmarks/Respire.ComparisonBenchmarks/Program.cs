using BenchmarkDotNet.Running;
using System.Reflection;
using StackExchange.Redis;

Console.WriteLine($"StackExchange.Redis: {typeof(ConnectionMultiplexer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
// BenchmarkDotNet may otherwise return success after a failed setup or generated build.
return summaries.Any(summary => summary.HasCriticalValidationErrors ||
    summary.Reports.Any(report => !report.Success)) ? 1 : 0;
