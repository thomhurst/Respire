using Microsoft.Extensions.Logging;
using ModularPipelines.Context;
using ModularPipelines.Modules;

namespace Keva.Pipeline.Modules;

public class PackageFilesRemovalModule : Module
{
    protected override async Task<IDictionary<string, object>> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Cleaning up old package files...");
        
        var packagesDirectory = new DirectoryInfo("../artifacts/packages");
        
        if (packagesDirectory.Exists)
        {
            var oldFiles = packagesDirectory.GetFiles("*.nupkg", SearchOption.AllDirectories);
            
            foreach (var file in oldFiles)
            {
                try
                {
                    file.Delete();
                    context.Logger.LogDebug("Deleted old package: {FileName}", file.Name);
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning(ex, "Failed to delete old package: {FileName}", file.Name);
                }
            }
            
            context.Logger.LogInformation("Cleaned up {Count} old package files", oldFiles.Length);
        }
        else
        {
            // Create the directory if it doesn't exist
            packagesDirectory.Create();
            context.Logger.LogInformation("Created packages directory: {Directory}", packagesDirectory.FullName);
        }
        
        return new Dictionary<string, object>();
    }
}