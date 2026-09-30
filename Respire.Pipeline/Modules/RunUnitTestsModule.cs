using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<BuildProjectsModule>]
public class RunUnitTestsModule : Module<CommandResult[]>
{
    // Keep below the pipeline's outer module deadline so diagnostic collection can finish.
    private const string HangDumpInactivityTimeout = "2m";
    private readonly IConfiguration _configuration;

    public RunUnitTestsModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Running unit tests...");

        var testProjects = _configuration.GetSection("TestProjects").Get<string[]>() ?? new[]
        {
            "../tests/Respire.Tests/Respire.Tests.csproj"
        };

        var results = new List<CommandResult>();
        var resultsDirectory = Path.GetFullPath("../artifacts/unit-tests");
        Directory.CreateDirectory(resultsDirectory);
        
        foreach (var project in testProjects)
        {
            var projectResultsDirectory = Path.Combine(resultsDirectory, Path.GetFileNameWithoutExtension(project));
            Directory.CreateDirectory(projectResultsDirectory);
            var result = await context.DotNet().Test(new DotNetTestOptions
            {
                Project = project,
                Configuration = "Release",
                NoBuild = true,
                ResultsDirectory = projectResultsDirectory,
                // HangDump measures time without test activity, not total suite duration.
                // Capture the stalled test sequence and stacks before the module's
                // outer timeout terminates the process without useful diagnostics.
                Arguments = ["--hangdump", "--hangdump-timeout", HangDumpInactivityTimeout, "--hangdump-type", "Mini", "--report-trx"]
            }, cancellationToken: cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}
