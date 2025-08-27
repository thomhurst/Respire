using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Modules;

namespace Keva.Pipeline.Modules.LocalMachine;

[DependsOn<PackagePathsParserModule>]
[DependsOn<AddLocalNugetSourceModule>]
[DependsOn<CreateLocalNugetFolderModule>]
public class UploadPackagesToLocalNuGetModule : Module<int>
{
    protected override async Task<int> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var packageFiles = await GetModule<PackagePathsParserModule>();
        var localNugetFolder = await GetModule<CreateLocalNugetFolderModule>();
        
        if (packageFiles.Value?.Length == 0)
        {
            context.Logger.LogWarning("No packages found to copy to local NuGet");
            return 0;
        }

        context.Logger.LogInformation("Copying {Count} packages to local NuGet folder", packageFiles.Value!.Length);
        
        var copiedCount = 0;
        
        foreach (var packageFile in packageFiles.Value!)
        {
            var destinationPath = Path.Combine(localNugetFolder.Value!.FullName, packageFile.Name);
            
            try
            {
                packageFile.CopyTo(destinationPath, overwrite: true);
                context.Logger.LogInformation("Copied {PackageName} to local NuGet", packageFile.Name);
                copiedCount++;
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex, "Failed to copy {PackageName} to local NuGet", packageFile.Name);
            }
        }

        context.Logger.LogInformation("Completed copying {Count} packages to local NuGet", copiedCount);
        
        return copiedCount;
    }
}