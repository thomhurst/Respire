using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Options;
using Respire.Pipeline.Settings;

namespace Respire.Pipeline.Modules;

// Publishing runs only for a published GitHub release (see release-draft.yml): the release's tag is
// the version, and GitHub has already created the tag and the release.
[DependsOn<PackagePathsParserModule>]
[DependsOn<VerifyCoreInternalsDependenciesModule>]
public class UploadPackagesToNugetModule : Module<CommandResult[]>
{
    private readonly IOptions<NuGetSettings> _nugetSettings;

    public UploadPackagesToNugetModule(IOptions<NuGetSettings> nugetSettings)
    {
        _nugetSettings = nugetSettings;
    }

    protected override void Configure(ModuleConfigurationBuilder module) => module
        .WithSkipWhen(_ => GetSkipDecision(Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME")));

    internal static SkipDecision GetSkipDecision(string? gitHubEventName) => gitHubEventName == "release"
        ? SkipDecision.DoNotSkip
        : SkipDecision.Skip("Not a published GitHub release");

    protected override async Task<CommandResult[]> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var packageFiles = await context.GetModule<PackagePathsParserModule>();
        var packages = packageFiles.ValueOrDefault ?? Array.Empty<FileInfo>();

        if (packages.Length == 0)
        {
            context.Logger.LogWarning("No packages found to upload");
            return Array.Empty<CommandResult>();
        }

        if (string.IsNullOrWhiteSpace(_nugetSettings.Value.ApiKey))
        {
            context.Logger.LogError("NuGet API key is not configured");
            throw new InvalidOperationException("NuGet API key is required for package upload");
        }

        context.Logger.LogInformation("Uploading {Count} packages to NuGet.org", packages.Length);

        var results = new List<CommandResult>();

        foreach (var packageFile in packages)
        {
            context.Logger.LogInformation("Uploading {PackageName}...", packageFile.Name);

            var result = await context.Tools.DotNet.NuGet.PushAsync(new DotNetNuGetPushOptions([packageFile.FullName])
            {
                Source = "https://api.nuget.org/v3/index.json",
                ApiKey = _nugetSettings.Value.ApiKey,
                // A failed release is retried by re-running its workflow; keep what already went up.
                SkipDuplicate = true
            }, cancellationToken: cancellationToken);

            results.Add(result);
        }

        context.Logger.LogInformation("Completed uploading packages to NuGet.org");

        return results.ToArray();
    }
}
