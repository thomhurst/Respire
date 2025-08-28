using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using Respire.Pipeline.Settings;

namespace Respire.Pipeline.Modules;

[DependsOn<PackagePathsParserModule>]
public class UploadPackagesToNugetModule : Module<CommandResult[]>
{
    private readonly IOptions<NuGetSettings> _nugetSettings;

    public UploadPackagesToNugetModule(IOptions<NuGetSettings> nugetSettings)
    {
        _nugetSettings = nugetSettings;
    }

    protected override async Task<CommandResult[]> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var packageFiles = await GetModule<PackagePathsParserModule>();
        
        if (packageFiles.Value?.Length == 0)
        {
            context.Logger.LogWarning("No packages found to upload");
            return Array.Empty<CommandResult>();
        }

        if (string.IsNullOrWhiteSpace(_nugetSettings.Value.ApiKey))
        {
            context.Logger.LogError("NuGet API key is not configured");
            throw new InvalidOperationException("NuGet API key is required for package upload");
        }

        context.Logger.LogInformation("Uploading {Count} packages to NuGet.org", packageFiles.Value!.Length);
        
        var results = new List<CommandResult>();

        foreach (var packageFile in packageFiles.Value!)
        {
            context.Logger.LogInformation("Uploading {PackageName}...", packageFile.Name);
            
            var result = await context.DotNet().Nuget.Push(new DotNetNugetPushOptions
            {
                Path = packageFile.FullName,
                Source = "https://api.nuget.org/v3/index.json",
                ApiKey = _nugetSettings.Value.ApiKey,
                SkipDuplicate = true
            }, cancellationToken);
            
            results.Add(result);
        }

        context.Logger.LogInformation("Completed uploading packages to NuGet.org");
        
        return results.ToArray();
    }
}