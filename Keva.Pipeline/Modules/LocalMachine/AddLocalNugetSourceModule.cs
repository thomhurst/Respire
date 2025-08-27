using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Keva.Pipeline.Modules.LocalMachine;

[DependsOn<CreateLocalNugetFolderModule>]
public class AddLocalNugetSourceModule : Module<CommandResult>
{
    protected override async Task<CommandResult> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var localNugetFolder = await GetModule<CreateLocalNugetFolderModule>();
        
        context.Logger.LogInformation("Adding local NuGet source...");
        
        var result = await context.DotNet().Nuget.AddSource(new DotNetNugetAddSourceOptions
        {
            Source = localNugetFolder.Value!.FullName,
            Name = "Keva-Local"
        }, cancellationToken);
        
        // It's okay if this fails - the source might already exist
        if (result.ExitCode != 0)
        {
            context.Logger.LogInformation("Local NuGet source may already exist (exit code: {ExitCode})", result.ExitCode);
        }
        else
        {
            context.Logger.LogInformation("Successfully added local NuGet source");
        }
        
        return result;
    }
}