using Microsoft.Extensions.Logging;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Options;

namespace Respire.Pipeline.Modules;

[DependsOn<NugetVersionGeneratorModule>]
public class BuildProjectsModule : Module<CommandResult>
{
    protected override async Task<CommandResult> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Building Respire solution...");
        
        return await context.Tools.DotNet.BuildAsync(new DotNetBuildOptions
        {
            ProjectSolution = "../Respire.slnx",
            Configuration = "Release",
            NoRestore = false,
            Properties = NugetVersionGeneratorModule.CreateVersionProperties(
                NugetVersionGeneratorModule.GetGeneratedVersion())
        }, cancellationToken: cancellationToken);
    }
}
