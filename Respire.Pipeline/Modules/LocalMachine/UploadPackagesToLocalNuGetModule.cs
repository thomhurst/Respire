using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules.LocalMachine;

[DependsOn<PackagePathsParserModule>]
[DependsOn<AddLocalNugetSourceModule>]
[DependsOn<CreateLocalNugetFolderModule>]
public class UploadPackagesToLocalNuGetModule : Module<int>
{
    protected override async Task<int> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var packageFiles = await context.GetModule<PackagePathsParserModule>();
        var localNugetFolder = await context.GetModule<CreateLocalNugetFolderModule>();
        var packages = packageFiles.ValueOrDefault ?? Array.Empty<FileInfo>();
        var localNugetDirectory = localNugetFolder.ValueOrDefault ?? throw new InvalidOperationException("Local NuGet folder was not created");
        
        if (packages.Length == 0)
        {
            context.Logger.LogWarning("No packages found to copy to local NuGet");
            return 0;
        }

        context.Logger.LogInformation("Copying {Count} packages to local NuGet folder", packages.Length);
        
        var copiedCount = 0;
        
        foreach (var packageFile in packages)
        {
            var destinationPath = Path.Combine(localNugetDirectory.FullName, packageFile.Name);
            
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
