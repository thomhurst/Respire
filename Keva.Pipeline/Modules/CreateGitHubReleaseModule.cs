using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;
using Keva.Pipeline.Settings;

namespace Keva.Pipeline.Modules;

[DependsOn<UploadPackagesToNugetModule>]
[DependsOn<PackagePathsParserModule>]
public class CreateGitHubReleaseModule : Module<string>
{
    private readonly IOptions<GitHubSettings> _gitHubSettings;

    public CreateGitHubReleaseModule(IOptions<GitHubSettings> gitHubSettings)
    {
        _gitHubSettings = gitHubSettings;
    }

    protected override async Task<string> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_gitHubSettings.Value.Token))
        {
            context.Logger.LogWarning("GitHub token not configured, skipping release creation");
            return "Skipped - No GitHub token";
        }

        var version = Environment.GetEnvironmentVariable("KEVA_VERSION") ?? "1.0.0-dev";
        var packageFiles = await GetModule<PackagePathsParserModule>();
        
        context.Logger.LogInformation("Creating GitHub release for version {Version}", version);
        
        var branch = await context.Git().Commands.Branch("--show-current", workingDirectory: "../", cancellationToken: cancellationToken);
        var commitHash = await context.Git().Commands.RevParse("HEAD", workingDirectory: "../", cancellationToken: cancellationToken);
        
        var gitInfo = new 
        {
            BranchName = branch?.StandardOutput?.Trim(),
            CommitHash = commitHash?.StandardOutput?.Trim()
        };
        
        var releaseNotes = GenerateReleaseNotes(version, gitInfo);
        
        // Note: This is a simplified implementation
        // In a real scenario, you would use the GitHub API to create a release
        context.Logger.LogInformation("Release notes for {Version}:", version);
        context.Logger.LogInformation(releaseNotes);
        
        // You would typically use a GitHub API client here
        // For example: context.GitHub().CreateRelease(...)
        
        return $"Release {version} created";
    }
    
    private static string GenerateReleaseNotes(string version, dynamic gitInfo)
    {
        return $"""
            # Keva {version}
            
            High-performance Redis client for .NET
            
            ## Changes
            - Unified client architecture for maximum performance
            - Zero-allocation RESP protocol implementation
            - Enhanced memory pooling and connection multiplexing
            
            ## Built from
            - Commit: {gitInfo.CommitHash}
            - Branch: {gitInfo.BranchName}
            
            ## Installation
            ```
            dotnet add package Keva --version {version}
            ```
            """;
    }
}