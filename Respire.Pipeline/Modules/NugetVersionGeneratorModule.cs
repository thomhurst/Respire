using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Context;
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

    // Set by modular-pipeline.yml on a published release, from the release's tag.
    public const string ReleaseVersionEnvironmentVariable = "RESPIRE_RELEASE_VERSION";

    private readonly IOptions<GitVersioningSettings> _settings;

    public NugetVersionGeneratorModule(IOptions<GitVersioningSettings> settings)
    {
        _settings = settings;
    }

    protected override async Task<string> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Generating version number...");

        var gitVersion = await GitVersionDetails.CreateAsync(_settings.Value, cancellationToken, context.Logger);

        Environment.SetEnvironmentVariable(VersionEnvironmentVariable, gitVersion.PackageVersion);
        Environment.SetEnvironmentVariable(LegacyVersionEnvironmentVariable, gitVersion.PackageVersion);
        Environment.SetEnvironmentVariable(BranchEnvironmentVariable, gitVersion.BranchName);
        Environment.SetEnvironmentVariable(CommitEnvironmentVariable, gitVersion.CommitHash);
        Environment.SetEnvironmentVariable(CommitHeightEnvironmentVariable, gitVersion.CommitHeight.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(BaseVersionEnvironmentVariable, gitVersion.BaseVersion);

        context.Logger.LogInformation(
            "Generated version {Version} from branch {Branch}, commit {Commit}, height {Height}, base {BaseVersion}, kind {Kind}",
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
    // The tag grammar the release workflows accept: an optional v prefix, X.Y.Z and an optional
    // prerelease suffix. Build metadata is not allowed, because NuGet ignores it.
    private static readonly Regex VersionTagRegex = new(
        @"\A[vV]?(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.Compiled);

    public static async Task<GitVersionDetails> CreateAsync(
        GitVersioningSettings settings,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var repositoryRoot = await GitCommand.RunAsync(Directory.GetCurrentDirectory(), cancellationToken, "rev-parse", "--show-toplevel");
        var branchName = await GetBranchNameAsync(repositoryRoot, cancellationToken);
        var releaseVersion = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") == "release"
            ? Environment.GetEnvironmentVariable(NugetVersionGeneratorModule.ReleaseVersionEnvironmentVariable) ?? string.Empty
            : null;
        return await CreateAsync(settings, repositoryRoot, branchName, cancellationToken, logger, releaseVersion);
    }

    internal static async Task<GitVersionDetails> CreateAsync(
        GitVersioningSettings settings,
        string repositoryRoot,
        string branchName,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        string? releaseVersion = null)
    {
        var commitHash = await GitCommand.RunAsync(repositoryRoot, cancellationToken, "rev-parse", "HEAD");
        var shortCommitHash = await GitCommand.RunAsync(repositoryRoot, cancellationToken, "rev-parse", "--short=8", "HEAD");

        if (releaseVersion is not null)
        {
            // A published GitHub release: its tag, which the owner may have edited on the draft, is the version.
            var release = ParseVersion(releaseVersion)
                ?? throw new InvalidOperationException(
                    $"The release version must look like 1.2.3 or 1.2.3-rc.1. Actual value: '{releaseVersion}'");

            return new GitVersionDetails(release.ToString(), release.ToString(), branchName, commitHash,
                shortCommitHash, 0, "release");
        }

        // Every other build is versioned as the release it would become: one step above the
        // nearest version tag, which GitHub creates when a release is published.
        var latestVersionTag = await GetLatestVersionTagAsync(repositoryRoot, cancellationToken, logger);
        var nextVersion = ParseVersion(latestVersionTag)?.Next()
            ?? ParseVersion(settings.BaseVersion)
            ?? throw new InvalidOperationException($"Versioning:BaseVersion must be a semantic version. Actual value: {settings.BaseVersion}");

        var commitHeight = await GetCommitHeightAsync(repositoryRoot,
            latestVersionTag is null ? "HEAD" : $"{latestVersionTag}..HEAD", cancellationToken);
        var isReleaseBranch = settings.ReleaseBranches.Contains(branchName, StringComparer.OrdinalIgnoreCase);
        var packageVersion = isReleaseBranch
            ? nextVersion.ToString()
            : CreatePrereleaseVersion(nextVersion.ToString(), branchName, commitHeight, shortCommitHash);

        return new GitVersionDetails(
            packageVersion,
            latestVersionTag ?? settings.BaseVersion,
            branchName,
            commitHash,
            shortCommitHash,
            commitHeight,
            nextVersion.Prerelease is null ? "patch" : "prerelease");
    }

    private static async Task<int> GetCommitHeightAsync(
        string repositoryRoot,
        string commitRange,
        CancellationToken cancellationToken)
    {
        var heightText = await GitCommand.RunAsync(repositoryRoot, cancellationToken, "rev-list", "--count", commitRange);
        return int.Parse(heightText, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    internal static async Task<string> GetBranchNameAsync(string repositoryRoot, CancellationToken cancellationToken)
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

        var branch = await GitCommand.RunAsync(repositoryRoot, cancellationToken, "branch", "--show-current");
        return string.IsNullOrWhiteSpace(branch) ? "detached" : NormalizeBranchName(branch);
    }

    internal static Task<string?> GetLatestVersionTagAsync(
        string repositoryRoot, CancellationToken cancellationToken, ILogger? logger = null)
        => IsolatedTagDescriber.DescribeAsync(repositoryRoot, cancellationToken, IsVersionTag, logger);

    private static bool IsVersionTag(string? value) => ParseVersion(value) is not null;

    internal static string? GetNextVersion(string? value) => ParseVersion(value)?.Next().ToString();

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

        var prerelease = match.Groups["prerelease"];

        return int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            ? new SemanticVersion(major, minor, patch, prerelease.Success ? prerelease.Value : null) : null;
    }

    private static string NormalizeBranchName(string branchName)
    {
        return branchName.Trim()
            .Replace("refs/heads/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("refs/tags/", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CreatePrereleaseVersion(string version, string branchName, int commitHeight, string shortCommitHash)
        // A hexadecimal hash can contain only digits with leading zeroes, which SemVer
        // forbids for numeric prerelease identifiers. The prefix makes every hash textual.
        // A version that is already a prerelease (2.0.0-rc.5) extends its label instead of adding a second hyphen.
        => $"{version}{(version.Contains('-', StringComparison.Ordinal) ? '.' : '-')}ci.{SanitizeNuGetIdentifier(branchName)}.{commitHeight}.g{shortCommitHash}";

    private static string SanitizeNuGetIdentifier(string value)
    {
        var sanitized = Regex.Replace(value.ToLowerInvariant(), "[^0-9a-z-]+", "-").Trim('-');

        if (sanitized.Length > 0 && sanitized.All(char.IsAsciiDigit))
        {
            return $"branch-{sanitized}";
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "branch" : sanitized;
    }

    private sealed record SemanticVersion(int Major, int Minor, int Patch, string? Prerelease)
    {
        // One step above this version: the next patch of a stable version (1.2.3 -> 1.2.4), or the
        // next candidate of a prerelease (2.0.0-rc.5 -> 2.0.0-rc.6, 2.0.0-beta -> 2.0.0-beta.1).
        // Minor, major and stable cuts are chosen by editing the draft release's tag.
        public SemanticVersion Next()
        {
            if (Prerelease is null)
            {
                return this with { Patch = checked(Patch + 1) };
            }

            var lastSeparator = Prerelease.LastIndexOf('.');
            var lastIdentifier = Prerelease[(lastSeparator + 1)..];

            if (lastIdentifier.Length > 0
                && lastIdentifier.All(char.IsAsciiDigit)
                && long.TryParse(lastIdentifier, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number < long.MaxValue)
            {
                var prefix = lastSeparator < 0 ? string.Empty : Prerelease[..(lastSeparator + 1)];
                return this with { Prerelease = string.Create(CultureInfo.InvariantCulture, $"{prefix}{number + 1}") };
            }

            return this with { Prerelease = $"{Prerelease}.1" };
        }

        public override string ToString()
        {
            var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
            return Prerelease is null ? core : $"{core}-{Prerelease}";
        }
    }
}
