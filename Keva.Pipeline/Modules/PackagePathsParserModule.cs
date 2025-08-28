using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.FileSystem;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<PackProjectsModule>]
public class PackagePathsParserModule : Module<FileInfo[]>
{
    protected override async Task<FileInfo[]> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Finding generated package files...");
        
        var packagesDirectory = new DirectoryInfo("../artifacts/packages");
        
        if (!packagesDirectory.Exists)
        {
            context.Logger.LogWarning("Packages directory does not exist: {Directory}", packagesDirectory.FullName);
            return Array.Empty<FileInfo>();
        }
        
        var packageFiles = packagesDirectory.GetFiles("*.nupkg", SearchOption.AllDirectories)
            .Where(f => !f.Name.Contains(".symbols."))
            .ToArray();
        
        context.Logger.LogInformation("Found {Count} package files:", packageFiles.Length);
        
        foreach (var file in packageFiles)
        {
            context.Logger.LogInformation("  - {FileName} ({Size} bytes)", file.Name, file.Length);
        }
        
        return packageFiles;
    }
}