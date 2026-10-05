using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Configuration;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<BuildProjectsModule>]
public class RunUnitTestsModule : Module<CommandResult[]>
{
    // The test verifies that this inactivity threshold leaves time before the module deadline.
    private const string HangDumpInactivityTimeout = "2m";
    private readonly IConfiguration _configuration;

    public RunUnitTestsModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // Preserve ModularPipelines 3.2.8's 30-minute default explicitly so the diagnostic
    // threshold can be checked against the deadline actually supplied to the engine.
    protected override ModuleConfiguration Configure() => ModuleConfiguration.Create()
        .WithTimeout(TimeSpan.FromMinutes(30))
        .Build();

    internal static string[] CreateDiagnosticsArguments() =>
        // Suspended async waits live on the managed heap. Mini dumps can name the running
        // test but omit the state machines needed to inspect its outstanding operations.
        ["--hangdump", "--hangdump-timeout", HangDumpInactivityTimeout, "--hangdump-type", "Heap", "--report-trx"];

    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Running unit tests...");

        // The library test suites run in the CI workflow; the pipeline only tests itself.
        var testProjects = _configuration.GetSection("TestProjects").Get<string[]>() ?? new[]
        {
            "../tests/Respire.Pipeline.Tests/Respire.Pipeline.Tests.csproj"
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
                Arguments = CreateDiagnosticsArguments()
            }, cancellationToken: cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}
