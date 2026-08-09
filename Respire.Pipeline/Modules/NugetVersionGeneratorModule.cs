using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Context;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using Respire.Pipeline.Settings;

namespace Respire.Pipeline.Modules;

public class NugetVersionGeneratorModule : Module<string>
{
    public const string VersionEnvironmentVariable = "RESPIRE_VERSION";
    public const string LegacyVersionEnvironmentVariable = "KEVA_VERSION";
    public const string BranchEnvironmentVariable = "RESPIRE_GIT_BRANCH";
    public const string CommitEnvironmentVariable = "RESPIRE_GIT_COMMIT";
    public const string CommitHeightEnvironmentVariable = "RESPIRE_GIT_HEIGHT";
    public const string BaseVersionEnvironmentVariable = "RESPIRE_GIT_BASE_VERSION";

    private readonly IOptions<GitVersioningSettings> _settings;

    public NugetVersionGeneratorModule(IOptions<GitVersioningSettings> settings)
    {
        _settings = settings;
    }

    protected override async Task<string?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Generating version number...");

        var gitVersion = await GitVersionDetails.CreateAsync(_settings.Value, cancellationToken);

        Environment.SetEnvironmentVariable(VersionEnvironmentVariable, gitVersion.PackageVersion);
        Environment.SetEnvironmentVariable(LegacyVersionEnvironmentVariable, gitVersion.PackageVersion);
        Environment.SetEnvironmentVariable(BranchEnvironmentVariable, gitVersion.BranchName);
        Environment.SetEnvironmentVariable(CommitEnvironmentVariable, gitVersion.CommitHash);
        Environment.SetEnvironmentVariable(CommitHeightEnvironmentVariable, gitVersion.CommitHeight.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(BaseVersionEnvironmentVariable, gitVersion.BaseVersion);

        context.Logger.LogInformation(
            "Generated version {Version} from branch {Branch}, commit {Commit}, height {Height}, base {BaseVersion}, increment {Increment}",
            gitVersion.PackageVersion,
            gitVersion.BranchName,
            gitVersion.ShortCommitHash,
            gitVersion.CommitHeight,
            gitVersion.BaseVersion,
            gitVersion.IncrementKind);

        return gitVersion.PackageVersion;
    }

    public static string GetGeneratedVersion()
    {
        return Environment.GetEnvironmentVariable(VersionEnvironmentVariable)
            ?? Environment.GetEnvironmentVariable(LegacyVersionEnvironmentVariable)
            ?? "0.1.0";
    }

    public static KeyValue[] CreateVersionProperties(string version)
    {
        var assemblyVersion = GetAssemblyCompatibleVersion(version);

        return new[]
        {
            new KeyValue("Version", version),
            new KeyValue("PackageVersion", version),
            new KeyValue("AssemblyVersion", assemblyVersion),
            new KeyValue("FileVersion", assemblyVersion),
            new KeyValue("InformationalVersion", CreateInformationalVersion(version))
        };
    }

    private static string CreateInformationalVersion(string version)
    {
        var commitHash = Environment.GetEnvironmentVariable(CommitEnvironmentVariable);
        return string.IsNullOrWhiteSpace(commitHash) ? version : $"{version}+{commitHash}";
    }

    private static string GetAssemblyCompatibleVersion(string version)
    {
        var suffixStart = version.IndexOfAny(new[] { '-', '+' });
        var coreVersion = suffixStart < 0 ? version : version[..suffixStart];
        var numericParts = coreVersion.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? Math.Clamp(number, 0, 65534)
                : 0)
            .ToList();

        while (numericParts.Count < 4)
        {
            numericParts.Add(0);
        }

        return string.Join('.', numericParts.Take(4));
    }
}

internal sealed record GitVersionDetails(
    string PackageVersion,
    string BaseVersion,
    string BranchName,
    string CommitHash,
    string ShortCommitHash,
    int CommitHeight,
    string IncrementKind)
{
    private static readonly Regex VersionTagRegex = new(
        @"^[vV]?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
        RegexOptions.Compiled);

    public static async Task<GitVersionDetails> CreateAsync(
        GitVersioningSettings settings,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await RunGitAsync(Directory.GetCurrentDirectory(), cancellationToken, "rev-parse", "--show-toplevel");
        var branchName = await GetBranchNameAsync(repositoryRoot, cancellationToken);
        var commitHash = await RunGitAsync(repositoryRoot, cancellationToken, "rev-parse", "HEAD");
        var shortCommitHash = await RunGitAsync(repositoryRoot, cancellationToken, "rev-parse", "--short=8", "HEAD");
        var latestVersionTag = await TryRunGitAsync(
            repositoryRoot,
            cancellationToken,
            "describe",
            "--tags",
            "--abbrev=0",
            "--match",
            "v[0-9]*",
            "--match",
            "[0-9]*");

        var baseVersion = ParseVersion(latestVersionTag) ?? ParseVersion(settings.BaseVersion);
        if (baseVersion is null)
        {
            throw new InvalidOperationException($"Versioning:BaseVersion must be a semantic version. Actual value: {settings.BaseVersion}");
        }

        var commitRange = latestVersionTag is null ? "HEAD" : $"{latestVersionTag}..HEAD";
        var commitHeight = await GetCommitHeightAsync(repositoryRoot, commitRange, cancellationToken);
        var versionIncrement = await GetVersionIncrementAsync(repositoryRoot, commitRange, cancellationToken);
        var coreVersion = baseVersion.Increment(versionIncrement, commitHeight);
        var isReleaseBranch = settings.ReleaseBranches.Contains(branchName, StringComparer.OrdinalIgnoreCase);
        var packageVersion = isReleaseBranch
            ? coreVersion.ToString()
            : $"{coreVersion}-ci.{SanitizeNuGetIdentifier(branchName)}.{commitHeight}.{shortCommitHash}";

        return new GitVersionDetails(
            packageVersion,
            baseVersion.ToString(),
            branchName,
            commitHash,
            shortCommitHash,
            commitHeight,
            versionIncrement.ToString().ToLowerInvariant());
    }

    private static async Task<int> GetCommitHeightAsync(
        string repositoryRoot,
        string commitRange,
        CancellationToken cancellationToken)
    {
        var heightText = await RunGitAsync(repositoryRoot, cancellationToken, "rev-list", "--count", commitRange);
        return int.Parse(heightText, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static async Task<VersionIncrement> GetVersionIncrementAsync(
        string repositoryRoot,
        string commitRange,
        CancellationToken cancellationToken)
    {
        var commitMessages = await RunGitAsync(repositoryRoot, cancellationToken, "log", "--format=%B%x1e", commitRange);
        return VersionIncrementExtensions.FromCommitMessages(commitMessages);
    }

    private static async Task<string> GetBranchNameAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        foreach (var value in new[]
                 {
                     Environment.GetEnvironmentVariable("PULL_REQUEST_BRANCH"),
                     Environment.GetEnvironmentVariable("GITHUB_HEAD_REF"),
                     Environment.GetEnvironmentVariable("GITHUB_REF_NAME")
                 })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return NormalizeBranchName(value);
            }
        }

        var branch = await TryRunGitAsync(repositoryRoot, cancellationToken, "branch", "--show-current")
            ?? await TryRunGitAsync(repositoryRoot, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD")
            ?? "detached";

        branch = NormalizeBranchName(branch);
        return branch == "HEAD" ? "detached" : branch;
    }

    private static SemanticVersion? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionTagRegex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        var patch = match.Groups["patch"].Success
            ? int.Parse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture)
            : 0;

        return new SemanticVersion(
            int.Parse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            patch);
    }

    private static string NormalizeBranchName(string branchName)
    {
        return branchName.Trim()
            .Replace("refs/heads/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("refs/tags/", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeNuGetIdentifier(string value)
    {
        var sanitized = Regex.Replace(value.ToLowerInvariant(), "[^0-9a-z-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "branch" : sanitized;
    }

    private static async Task<string?> TryRunGitAsync(
        string repositoryRoot,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        try
        {
            return await RunGitAsync(repositoryRoot, cancellationToken, arguments);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<string> RunGitAsync(
        string repositoryRoot,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start git.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private enum VersionIncrement
    {
        None,
        Patch,
        Minor,
        Major
    }

    private static class VersionIncrementExtensions
    {
        private static readonly Regex IncrementMarkerRegex = new(
            @"\+semver:\s*(?<increment>major|breaking|minor|feature|patch|fix|none|skip)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static VersionIncrement FromCommitMessages(string commitMessages)
        {
            VersionIncrement? increment = null;

            foreach (Match match in IncrementMarkerRegex.Matches(commitMessages))
            {
                var marker = match.Groups["increment"].Value;
                var candidate = marker.ToLowerInvariant() switch
                {
                    "major" or "breaking" => VersionIncrement.Major,
                    "minor" or "feature" => VersionIncrement.Minor,
                    "patch" or "fix" => VersionIncrement.Patch,
                    "none" or "skip" => VersionIncrement.None,
                    _ => VersionIncrement.Patch
                };

                if (increment is null || (int)candidate > (int)increment.Value)
                {
                    increment = candidate;
                }
            }

            return increment ?? VersionIncrement.Patch;
        }
    }

    private sealed record SemanticVersion(int Major, int Minor, int Patch)
    {
        public SemanticVersion Increment(VersionIncrement increment, int commitHeight)
        {
            return increment switch
            {
                VersionIncrement.None => this,
                VersionIncrement.Major => this with
                {
                    Major = Major + 1,
                    Minor = 0,
                    Patch = commitHeight
                },
                VersionIncrement.Minor => this with
                {
                    Minor = Minor + 1,
                    Patch = commitHeight
                },
                _ => this with { Patch = Patch + commitHeight }
            };
        }

        public override string ToString()
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Major}.{Minor}.{Patch}");
        }
    }
}
