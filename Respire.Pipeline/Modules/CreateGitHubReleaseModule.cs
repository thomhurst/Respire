using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Configuration;
using ModularPipelines.Context;
using ModularPipelines.GitHub;
using ModularPipelines.GitHub.Extensions;
using ModularPipelines.GitHub.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using Octokit;
using Respire.Pipeline.Settings;

namespace Respire.Pipeline.Modules;

[DependsOn<UploadPackagesToNugetModule>]
[DependsOn<PackagePathsParserModule>]
[DependsOn<NugetVersionGeneratorModule>]
public class CreateGitHubReleaseModule : Module<string>
{
    private readonly IOptions<GitHubOptions> _gitHubOptions;
    private readonly IOptions<GitHubSettings> _gitHubSettings;

    public CreateGitHubReleaseModule(
        IOptions<GitHubOptions> gitHubOptions,
        IOptions<GitHubSettings> gitHubSettings)
    {
        _gitHubOptions = gitHubOptions;
        _gitHubSettings = gitHubSettings;
    }

    protected override ModuleConfiguration Configure() => ModuleConfiguration.Create()
        .WithSkipWhen(async context =>
        {
            if (context.GetModuleIfRegistered<UploadPackagesToNugetModule>() is not { } uploadToNuGetModule)
            {
                return SkipDecision.Skip("UploadPackagesToNugetModule not registered");
            }

            var result = await uploadToNuGetModule;
            if (result.IsSkipped)
            {
                return SkipDecision.Skip("UploadPackagesToNugetModule was skipped");
            }

            return result.ValueOrDefault is { Length: > 0 }
                ? SkipDecision.DoNotSkip
                : SkipDecision.Skip("No packages were uploaded to NuGet");
        })
        .Build();

    protected override async Task<string?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var gitHub = context.GitHub();
        if (string.IsNullOrWhiteSpace(_gitHubOptions.Value.AccessToken)
            && string.IsNullOrWhiteSpace(gitHub.EnvironmentVariables.Token))
        {
            context.Logger.LogWarning("GitHub token not configured, skipping release creation");
            return "Skipped - No GitHub token";
        }

        var repository = ResolveRepository(gitHub, _gitHubSettings.Value);
        var versionModule = await context.GetModule<NugetVersionGeneratorModule>();
        var version = versionModule.ValueOrDefault ?? NugetVersionGeneratorModule.GetGeneratedVersion();
        var tagName = CreateReleaseTag(version);
        var targetCommitish = ResolveTargetCommitish(gitHub.EnvironmentVariables);
        var packageFiles = (await context.GetModule<PackagePathsParserModule>()).ValueOrDefault ?? Array.Empty<FileInfo>();

        context.Logger.LogInformation("Creating GitHub release for version {Version}", version);

        var existingRelease = await GetReleaseByTagAsync(gitHub.Client, repository, tagName);
        if (existingRelease is not null)
        {
            context.Logger.LogInformation(
                "GitHub release {TagName} already exists at {ReleaseUrl}, skipping release creation",
                tagName,
                existingRelease.HtmlUrl);

            return existingRelease.HtmlUrl;
        }

        var latestRelease = await GetLatestReleaseAsync(gitHub.Client, repository);
        var releaseNotes = await GenerateReleaseNotesAsync(
            gitHub.Client,
            repository,
            new GenerateReleaseNotesRequest(tagName)
            {
                PreviousTagName = latestRelease?.TagName,
                TargetCommitish = targetCommitish
            });

        var createdRelease = await CreateReleaseAsync(
            gitHub.Client,
            repository,
            new NewRelease(tagName)
            {
                Name = version,
                Body = CreateReleaseBody(releaseNotes.Body, version, packageFiles),
                Draft = false,
                GenerateReleaseNotes = false,
                Prerelease = IsPrerelease(version),
                TargetCommitish = targetCommitish
            });

        context.Logger.LogInformation(
            "Created GitHub release {TagName} at {ReleaseUrl}",
            tagName,
            createdRelease.HtmlUrl);

        return createdRelease.HtmlUrl;
    }

    internal static string CreateReleaseTag(string version)
    {
        return version.StartsWith('v') || version.StartsWith('V')
            ? version
            : string.Create(CultureInfo.InvariantCulture, $"v{version}");
    }

    internal static bool IsPrerelease(string version)
    {
        return version.Contains('-', StringComparison.Ordinal);
    }

    internal static string CreateReleaseBody(string generatedBody, string version, IReadOnlyCollection<FileInfo> packageFiles)
    {
        var body = string.IsNullOrWhiteSpace(generatedBody)
            ? "GitHub did not return generated release notes for this version."
            : generatedBody.TrimEnd();

        if (packageFiles.Count == 0)
        {
            return body;
        }

        var packageIds = packageFiles
            .Select(file => GetPackageId(file.Name, version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (packageIds.Length == 0)
        {
            return body;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            {body}

            ## NuGet packages

            ```bash
            {string.Join(Environment.NewLine, packageIds.Select(packageId => $"dotnet add package {packageId} --version {version}"))}
            ```
            """);
    }

    private static GitHubRepositoryReference ResolveRepository(IGitHub gitHub, GitHubSettings settings)
    {
        if (long.TryParse(gitHub.EnvironmentVariables.RepositoryId, NumberStyles.None, CultureInfo.InvariantCulture, out var repositoryId))
        {
            return new GitHubRepositoryReference(repositoryId, Owner: null, Name: null);
        }

        var owner = FirstNonWhiteSpace(settings.Owner, gitHub.RepositoryInfo.Owner);
        var name = FirstNonWhiteSpace(settings.Repository, gitHub.RepositoryInfo.RepositoryName);

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                "GitHub repository could not be resolved. Set GitHub:Owner and GitHub:Repository or run in GitHub Actions.");
        }

        return new GitHubRepositoryReference(RepositoryId: null, owner, name);
    }

    private static string ResolveTargetCommitish(IGitHubEnvironmentVariables environmentVariables)
    {
        return FirstNonWhiteSpace(
                Environment.GetEnvironmentVariable(NugetVersionGeneratorModule.CommitEnvironmentVariable),
                environmentVariables.Sha,
                Environment.GetEnvironmentVariable(NugetVersionGeneratorModule.BranchEnvironmentVariable),
                environmentVariables.RefName)
            ?? "main";
    }

    private static async Task<Release?> GetReleaseByTagAsync(
        IGitHubClient client,
        GitHubRepositoryReference repository,
        string tagName)
    {
        try
        {
            return repository.RepositoryId is { } repositoryId
                ? await client.Repository.Release.Get(repositoryId, tagName)
                : await client.Repository.Release.Get(repository.Owner!, repository.Name!, tagName);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private static async Task<Release?> GetLatestReleaseAsync(
        IGitHubClient client,
        GitHubRepositoryReference repository)
    {
        try
        {
            return repository.RepositoryId is { } repositoryId
                ? await client.Repository.Release.GetLatest(repositoryId)
                : await client.Repository.Release.GetLatest(repository.Owner!, repository.Name!);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private static Task<GeneratedReleaseNotes> GenerateReleaseNotesAsync(
        IGitHubClient client,
        GitHubRepositoryReference repository,
        GenerateReleaseNotesRequest request)
    {
        return repository.RepositoryId is { } repositoryId
            ? client.Repository.Release.GenerateReleaseNotes(repositoryId, request)
            : client.Repository.Release.GenerateReleaseNotes(repository.Owner!, repository.Name!, request);
    }

    private static Task<Release> CreateReleaseAsync(
        IGitHubClient client,
        GitHubRepositoryReference repository,
        NewRelease release)
    {
        return repository.RepositoryId is { } repositoryId
            ? client.Repository.Release.Create(repositoryId, release)
            : client.Repository.Release.Create(repository.Owner!, repository.Name!, release);
    }

    private static string GetPackageId(string fileName, string version)
    {
        var packageId = Path.GetFileNameWithoutExtension(fileName);
        var versionSuffix = $".{version}";

        return packageId.EndsWith(versionSuffix, StringComparison.OrdinalIgnoreCase)
            ? packageId[..^versionSuffix.Length]
            : packageId;
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record GitHubRepositoryReference(long? RepositoryId, string? Owner, string? Name);
}
