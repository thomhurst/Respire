using BenchmarkDotNet.Running;
using System.Reflection;
using StackExchange.Redis;

Console.WriteLine($"StackExchange.Redis: {typeof(ConnectionMultiplexer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
if (args is ["--profile-pubsub", var output])
{
    await Respire.ComparisonBenchmarks.PubSubComparisonBenchmarks.ProfileAsync(output);
    return 0;
}
var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
// BenchmarkDotNet may otherwise return success after a failed setup or generated build.
return summaries.Any(summary => summary.HasCriticalValidationErrors ||
    summary.Reports.Any(report => !report.Success)) ? 1 : 0;
