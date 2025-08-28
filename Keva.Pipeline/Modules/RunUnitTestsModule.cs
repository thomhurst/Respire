using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Enums;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<BuildProjectsModule>]
public class RunUnitTestsModule : Module<CommandResult[]>
{
    protected override async Task<CommandResult[]> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Running unit tests...");

        var testProjects = context.Configuration.GetSection("TestProjects").Get<string[]>() ?? new[]
        {
            "../tests/Respire.Tests/Respire.Tests.csproj"
        };

        var results = new List<CommandResult>();
        
        foreach (var project in testProjects)
        {
            var result = await context.DotNet().Test(new DotNetTestOptions
            {
                ProjectSolutionDirectoryDllExe = project,
                Configuration = Configuration.Release,
                NoBuild = true,
                Verbosity = "normal"
            }, cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}