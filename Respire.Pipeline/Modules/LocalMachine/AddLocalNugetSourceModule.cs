using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules.LocalMachine;

[DependsOn<CreateLocalNugetFolderModule>]
public class AddLocalNugetSourceModule : Module<string>
{
    protected override async Task<string?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var folder = await context.GetModule<CreateLocalNugetFolderModule>();
        var directory = folder.ValueOrDefault
            ?? throw new InvalidOperationException("Local NuGet folder was not created");

        var name = await LocalNugetSource.EnsureRegisteredAsync(directory.FullName, cancellationToken);
        context.Logger.LogInformation("Local NuGet source {Name} is enabled at {Path}", name, directory.FullName);
        return name;
    }
}
