using System.Diagnostics;
using System.Globalization;
using System.Text;
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
        @"\A[vV]?(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)\z",
        RegexOptions.Compiled);

    public static async Task<GitVersionDetails> CreateAsync(
        GitVersioningSettings settings,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await RunGitAsync(Directory.GetCurrentDirectory(), cancellationToken, "rev-parse", "--show-toplevel");
        var branchName = await GetBranchNameAsync(repositoryRoot, cancellationToken);
        var commitHash = await RunGitAsync(repositoryRoot, cancellationToken, "rev-parse", "HEAD");
        var shortCommitHash = await RunGitAsync(repositoryRoot, cancellationToken, "rev-parse", "--short=8", "HEAD");
        var latestVersionTag = await GetLatestStableVersionTagAsync(repositoryRoot, cancellationToken);

        var baseVersion = ParseVersion(latestVersionTag) ?? ParseVersion(settings.BaseVersion);
        if (baseVersion is null)
        {
            throw new InvalidOperationException($"Versioning:BaseVersion must be a semantic version. Actual value: {settings.BaseVersion}");
        }

        var commitRange = latestVersionTag is null ? "HEAD" : $"{latestVersionTag}..HEAD";
        var commitHeight = await GetCommitHeightAsync(repositoryRoot, commitRange, cancellationToken);
        var versionIncrement = await GetVersionIncrementAsync(repositoryRoot, commitRange, commitHeight, cancellationToken);
        var coreVersion = baseVersion.Increment(versionIncrement, commitHeight);
        var isReleaseBranch = settings.ReleaseBranches.Contains(branchName, StringComparer.OrdinalIgnoreCase);
        var packageVersion = isReleaseBranch
            ? coreVersion.ToString()
            : CreatePrereleaseVersion(coreVersion.ToString(), branchName, commitHeight, shortCommitHash);

        return new GitVersionDetails(
            packageVersion,
            baseVersion.ToString(),
            branchName,
            commitHash,
            shortCommitHash,
            commitHeight,
            versionIncrement.Increment.ToString().ToLowerInvariant());
    }

    private static async Task<int> GetCommitHeightAsync(
        string repositoryRoot,
        string commitRange,
        CancellationToken cancellationToken)
    {
        var heightText = await RunGitAsync(repositoryRoot, cancellationToken, "rev-list", "--count", commitRange);
        return int.Parse(heightText, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    internal static async Task<VersionIncrementResult> GetVersionIncrementAsync(
        string repositoryRoot,
        string commitRange,
        int commitHeight,
        CancellationToken cancellationToken)
    {
        var commitMessages = await RunGitAsync(repositoryRoot, cancellationToken, "log", "--reverse", "--format=%B%x1e", commitRange);
        return VersionIncrementResult.FromCommitMessages(commitMessages, commitHeight);
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

        var branch = await RunGitAsync(repositoryRoot, cancellationToken, "branch", "--show-current");
        return string.IsNullOrWhiteSpace(branch) ? "detached" : NormalizeBranchName(branch);
    }

    internal static async Task<string?> GetLatestStableVersionTagAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        // Read local refs directly: clone/fetch advertisements can hide release tags.
        // Capture the calling worktree's HEAD and shared object/shallow paths together.
        var metadata = (await RunGitAsync(repositoryRoot, cancellationToken, "rev-parse",
            "--show-object-format", "--path-format=absolute", "--git-path", "objects", "--git-path", "shallow", "HEAD"))
            .Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (metadata.Length != 4) throw new InvalidOperationException("Git returned unexpected repository metadata.");
        var (objectFormat, objects, shallow, head) = (metadata[0], metadata[1], metadata[2], metadata[3]);
        var localRefs = await RunGitAsync(repositoryRoot, cancellationToken,
            "for-each-ref", "--format=%(objectname) %(refname)", "refs/tags", "refs/replace");
        var updates = new StringBuilder();
        foreach (var line in localRefs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(' ');
            var name = line[(separator + 1)..];
            if (name.StartsWith("refs/tags/", StringComparison.Ordinal) && ParseVersion(name[10..]) is null) continue;
            updates.Append("update ").Append(name).Append(' ').Append(line.AsSpan(0, separator)).Append('\n');
        }

        // Isolate filtered refs while borrowing objects. Include replacements and the
        // shallow boundary so Git walks the same local history without touching source refs.
        // Every argument list remains constant-size; ref updates travel over stdin.
        var temporary = Directory.CreateTempSubdirectory("respire-stable-version-");
        try
        {
            await RunGitAsync(temporary.FullName, cancellationToken,
                "init", "--bare", "--quiet", "--template=", $"--object-format={objectFormat}");
            await File.WriteAllTextAsync(Path.Combine(temporary.FullName, "objects", "info", "alternates"),
                objects + "\n", cancellationToken);
            if (File.Exists(shallow)) File.Copy(shallow, Path.Combine(temporary.FullName, "shallow"));
            if (updates.Length != 0)
                await RunGitWithInputAsync(temporary.FullName, cancellationToken, updates.ToString(), "update-ref", "--stdin");

            // Git retains native distance, merge-history and annotated-tag precedence.
            // --always reports a commit hash only when no retained tag is reachable;
            // unlike catching Git failures, this leaves repository errors observable.
            var selected = await RunGitAsync(temporary.FullName, cancellationToken,
                "describe", "--tags", "--abbrev=0", "--always", head);
            return ParseVersion(selected) is null ? null : selected;
        }
        finally
        {
            // Temporary metadata cleanup is best-effort: a locked file must not replace
            // the Git failure/cancellation, or prevent returning an already computed version.
            try
            {
                foreach (var file in temporary.EnumerateFiles("*", SearchOption.AllDirectories))
                    file.Attributes &= ~FileAttributes.ReadOnly;
                temporary.Delete(recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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

        return int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            ? new SemanticVersion(major, minor, patch) : null;
    }

    private static string NormalizeBranchName(string branchName)
    {
        return branchName.Trim()
            .Replace("refs/heads/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("refs/tags/", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CreatePrereleaseVersion(string coreVersion, string branchName, int commitHeight, string shortCommitHash)
        // A hexadecimal hash can contain only digits with leading zeroes, which SemVer
        // forbids for numeric prerelease identifiers. The prefix makes every hash textual.
        => $"{coreVersion}-ci.{SanitizeNuGetIdentifier(branchName)}.{commitHeight}.g{shortCommitHash}";

    private static string SanitizeNuGetIdentifier(string value)
    {
        var sanitized = Regex.Replace(value.ToLowerInvariant(), "[^0-9a-z-]+", "-").Trim('-');
        if (sanitized.Length > 0 && sanitized.All(char.IsAsciiDigit)) return $"branch-{sanitized}";
        return string.IsNullOrWhiteSpace(sanitized) ? "branch" : sanitized;
    }

    private static Task<string> RunGitAsync(
        string repositoryRoot,
        CancellationToken cancellationToken,
        params string[] arguments)
        => RunGitWithInputAsync(repositoryRoot, cancellationToken, null, arguments);

    private static async Task<string> RunGitWithInputAsync(
        string repositoryRoot,
        CancellationToken cancellationToken,
        string? input,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                RedirectStandardInput = input is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
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

        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Join this owned process before the caller removes its temporary repository.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The process already exited. */ }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdoutTask, stderrTask); }
            catch (OperationCanceledException) { }
            throw;
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private sealed record SemanticVersion(int Major, int Minor, int Patch)
    {
        public SemanticVersion Increment(VersionIncrementResult increment, int commitHeight)
        {
            return increment.Increment switch
            {
                VersionIncrement.None => this,
                VersionIncrement.Major => this with
                {
                    Major = Major + 1,
                    Minor = 0,
                    Patch = increment.PatchHeight
                },
                VersionIncrement.Minor => this with
                {
                    Minor = Minor + 1,
                    Patch = increment.PatchHeight
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

internal enum VersionIncrement
{
    None,
    Patch,
    Minor,
    Major
}

internal sealed record VersionIncrementResult(VersionIncrement Increment, int PatchHeight)
{
    private static readonly Regex IncrementMarkerRegex = new(
        @"\+semver:\s*(?<increment>major|breaking|minor|feature|patch|fix|none|skip)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static VersionIncrementResult FromCommitMessages(string commitMessages, int commitHeight)
    {
        var commits = commitMessages
            .Split('\x1e', StringSplitOptions.TrimEntries);
        // Git terminates every record with a separator. Only the final split entry is synthetic.
        var commitCount = commits.Length > 1 && commits[^1].Length == 0 ? commits.Length - 1 : commits.Length;

        VersionIncrement? selectedIncrement = null;
        var selectedCommitIndex = -1;

        for (var commitIndex = 0; commitIndex < commitCount; commitIndex++)
        {
            foreach (Match match in IncrementMarkerRegex.Matches(commits[commitIndex]))
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

                if (selectedIncrement is null
                    || (int)candidate > (int)selectedIncrement.Value
                    || candidate == selectedIncrement.Value)
                {
                    selectedIncrement = candidate;
                    selectedCommitIndex = commitIndex;
                }
            }
        }

        var increment = selectedIncrement ?? VersionIncrement.Patch;
        var patchHeight = increment switch
        {
            VersionIncrement.Major or VersionIncrement.Minor => Math.Max(commitCount - selectedCommitIndex - 1, 0),
            VersionIncrement.None => 0,
            _ => commitHeight
        };

        return new VersionIncrementResult(increment, patchHeight);
    }
}
