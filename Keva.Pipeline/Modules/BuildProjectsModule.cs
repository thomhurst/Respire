using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Enums;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Keva.Pipeline.Modules;

[DependsOn<NugetVersionGeneratorModule>]
public class BuildProjectsModule : Module<CommandResult>
{
    protected override async Task<CommandResult> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Building Keva solution...");
        
        return await context.DotNet().Build(new DotNetBuildOptions
        {
            ProjectSolution = "../Keva.sln",
            Configuration = Configuration.Release,
            NoRestore = false
        }, cancellationToken);
    }
}