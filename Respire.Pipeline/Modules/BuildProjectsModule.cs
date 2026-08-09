using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<NugetVersionGeneratorModule>]
public class BuildProjectsModule : Module<CommandResult>
{
    protected override async Task<CommandResult?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Building Respire solution...");
        
        return await context.DotNet().Build(new DotNetBuildOptions
        {
            ProjectSolution = "../Respire.slnx",
            Configuration = "Release",
            NoRestore = false
        }, cancellationToken: cancellationToken);
    }
}
