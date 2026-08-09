using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<RunUnitTestsModule>]
public class RunBenchmarkModule : Module<CommandResult[]>
{
    private readonly IConfiguration _configuration;

    public RunBenchmarkModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Running performance benchmarks...");

        var benchmarkProjects = _configuration.GetSection("BenchmarkProjects").Get<string[]>() ?? new[]
        {
            "../benchmarks/Respire.Benchmarks/Respire.Benchmarks.csproj"
        };

        var results = new List<CommandResult>();
        
        foreach (var project in benchmarkProjects)
        {
            try
            {
                var result = await context.DotNet().Run(new DotNetRunOptions
                {
                    Project = project,
                    Configuration = "Release",
                    NoBuild = true
                }, cancellationToken: cancellationToken);
                
                results.Add(result);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex, "Benchmark failed for {Project}", project);
                // Continue with other benchmarks even if one fails
            }
        }

        return results.ToArray();
    }
}
