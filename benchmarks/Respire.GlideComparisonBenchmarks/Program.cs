using BenchmarkDotNet.Running;

// Valkey.Glide 1.1.0 stamps its assembly with a placeholder version, so the resolved
// package version is recorded from project.assets.json by the workflow instead.
var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
// BenchmarkDotNet may otherwise return success after a failed setup or generated build.
return summaries.Any(summary => summary.HasCriticalValidationErrors ||
    summary.Reports.Any(report => !report.Success)) ? 1 : 0;
