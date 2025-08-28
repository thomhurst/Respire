using Microsoft.Extensions.Logging;
using ModularPipelines.Context;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules.LocalMachine;

public class CreateLocalNugetFolderModule : Module<DirectoryInfo>
{
    protected override async Task<DirectoryInfo> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Creating local NuGet folder for development...");
        
        var localNugetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "local-packages");
        var localNugetDirectory = new DirectoryInfo(localNugetPath);
        
        if (!localNugetDirectory.Exists)
        {
            localNugetDirectory.Create();
            context.Logger.LogInformation("Created local NuGet folder: {Path}", localNugetDirectory.FullName);
        }
        else
        {
            context.Logger.LogInformation("Local NuGet folder already exists: {Path}", localNugetDirectory.FullName);
        }
        
        return localNugetDirectory;
    }
}