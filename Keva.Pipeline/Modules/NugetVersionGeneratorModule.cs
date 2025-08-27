using Microsoft.Extensions.Logging;
using ModularPipelines.Context;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;

namespace Keva.Pipeline.Modules;

public class NugetVersionGeneratorModule : Module<string>
{
    protected override async Task<string> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Generating version number...");
        
        // Get branch and commit info
        var branch = await context.Git().Commands.Branch("--show-current", workingDirectory: "../", cancellationToken: cancellationToken);
        var commitHash = await context.Git().Commands.RevParse("HEAD", workingDirectory: "../", cancellationToken: cancellationToken);
        
        var gitInfo = new 
        {
            BranchName = branch?.StandardOutput?.Trim(),
            CommitHash = commitHash?.StandardOutput?.Trim()
        };
        
        // Generate version based on git information
        var version = GenerateVersion(gitInfo);
        
        context.Logger.LogInformation("Generated version: {Version}", version);
        
        // Set as environment variable for use in other modules
        Environment.SetEnvironmentVariable("KEVA_VERSION", version);
        
        return version;
    }
    
    private static string GenerateVersion(dynamic gitInfo)
    {
        var baseVersion = "1.0.0";
        
        // If we have commit info, add pre-release suffix
        if (!string.IsNullOrEmpty(gitInfo.CommitHash))
        {
            var shortHash = gitInfo.CommitHash[..Math.Min(7, gitInfo.CommitHash.Length)];
            
            // Check if we're on main/master branch
            if (gitInfo.BranchName is "main" or "master")
            {
                return $"{baseVersion}";
            }
            
            // Pre-release version for other branches
            return $"{baseVersion}-{gitInfo.BranchName?.Replace("/", "-")}-{shortHash}";
        }
        
        return $"{baseVersion}-dev";
    }
}