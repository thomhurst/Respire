using System.Diagnostics;
using Respire.Pipeline.Modules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class StableVersionTagTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BranchLookupDistinguishesDetachedHeadFromGitFailure(bool detached)
    {
        using var repository = new TestRepository();
        var branchVariables = new[] { "PULL_REQUEST_BRANCH", "GITHUB_HEAD_REF", "GITHUB_REF_NAME" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in branchVariables.Keys) Environment.SetEnvironmentVariable(name, null);
            await Assert.That(() => GitVersionDetails.GetBranchNameAsync(repository.Path, CancellationToken.None))
                .ThrowsExactly<InvalidOperationException>();
            await repository.InitializeAsync();
            if (detached) await repository.Git("checkout", "--detach");
            await Assert.That(await GitVersionDetails.GetBranchNameAsync(repository.Path, CancellationToken.None))
                .IsEqualTo(detached ? "detached" : "main");
        }
        finally
        {
            foreach (var (name, value) in branchVariables) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Test]
    public async Task GitFailureDoesNotBecomeMissingStableTag()
    {
        using var repository = new TestRepository();
        // An existing directory without a repository is a Git failure, not a tag miss.
        await Assert.That(() => GitVersionDetails.GetLatestStableVersionTagAsync(repository.Path, CancellationToken.None))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    [NotInParallel]
    public async Task LargePrereleaseHistoryDoesNotGrowDescribeArguments()
    {
        using var repository = new TestRepository();
        await repository.InitializeAsync();
        await repository.Git("tag", "v1.2.3");
        await repository.CommitAsync("prereleases");
        var head = await repository.Git("rev-parse", "HEAD");
        // Populate refs in one process; setup must not hide linear process creation
        // in the version lookup itself. These exclusions would exceed Windows argv limits.
        var updates = string.Join('\n', Enumerable.Range(0, 512)
            .Select(index => $"create refs/tags/v2.0.0-preview.{index:D4}.{new string('x', 64)} {head}")) + "\n";
        await repository.GitWithInput(updates, "update-ref", "--stdin");
        var trace = System.IO.Path.Combine(repository.Path, "git-trace.jsonl");
        var previousTrace = Environment.GetEnvironmentVariable("GIT_TRACE2_EVENT");
        try
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", trace);
            await Assert.That(await repository.SelectAsync()).IsEqualTo("v1.2.3");
        }
        finally { Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", previousTrace); }
        var starts = File.ReadLines(trace).Count(line =>
        {
            using var record = System.Text.Json.JsonDocument.Parse(line);
            return record.RootElement.GetProperty("event").GetString() == "start";
        });
        await Assert.That(starts).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NearestLowerVersionWinsOverOlderHigherVersion(bool annotated)
    {
        using var repository = new TestRepository();
        await repository.InitializeAsync();
        await repository.TagAsync("v9.0.0", annotated);
        await repository.CommitAsync("nearer release");
        await repository.TagAsync("v1.2.3", annotated);
        await repository.CommitAsync("unreleased");
        await Assert.That(await repository.SelectAsync()).IsEqualTo("v1.2.3");
    }

    [Test]
    public async Task ReachableReleaseOnMergedBranchIsConsidered()
    {
        using var repository = new TestRepository();
        await repository.InitializeAsync();
        await repository.Git("tag", "v9.0.0");
        await repository.Git("checkout", "-b", "release-side");
        await repository.CommitAsync("side release");
        await repository.TagAsync("v1.2.3", annotated: true);
        await repository.Git("checkout", "main");
        await repository.CommitAsync("main work");
        await repository.Git("merge", "--no-ff", "release-side", "-m", "merge release");
        await Assert.That(await repository.SelectAsync()).IsEqualTo("v1.2.3");
    }

    [Test]
    public async Task AnnotatedTagWinsOverLightweightTagOnSameCommit()
    {
        using var repository = new TestRepository();
        await repository.InitializeAsync();
        await repository.Git("tag", "v9.0.0");
        await repository.TagAsync("v1.2.3", annotated: true);
        await Assert.That(await repository.SelectAsync()).IsEqualTo("v1.2.3");
    }

    [Test]
    public async Task UnreachableStableTagDoesNotSupplyBaseVersion()
    {
        using var repository = new TestRepository();
        await repository.InitializeAsync();
        await repository.Git("checkout", "-b", "unmerged");
        await repository.CommitAsync("unreachable release");
        await repository.Git("tag", "v1.2.3");
        await repository.Git("checkout", "main");
        await repository.CommitAsync("main work");
        await Assert.That(await repository.SelectAsync()).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SkipsSuffixedAndIncompleteTags(bool hasStableTag)
    {
        using var repository = new TestRepository();
        await repository.InitializeAsync();
        if (hasStableTag) await repository.Git("tag", "V1.2.3");
        foreach (var tag in new[] { "v1.3.0-beta.1", "v1.4.0+build", "v1.5", "v999999999999999.0.0" })
        {
            await repository.CommitAsync(tag);
            await repository.Git("tag", tag);
        }
        await Assert.That(await repository.SelectAsync()).IsEqualTo(hasStableTag ? "V1.2.3" : null);
        await repository.CommitAsync("+semver:minor");
        await repository.Git("commit", "--allow-empty", "--allow-empty-message", "-m", "");
        var increment = await GitVersionDetails.GetVersionIncrementAsync(repository.Path, "HEAD~2..HEAD", 2, CancellationToken.None);
        await Assert.That(increment.Increment).IsEqualTo(VersionIncrement.Minor);
        await Assert.That(increment.PatchHeight).IsEqualTo(1);
    }

    private sealed class TestRepository : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("respire-version-tags-");
        internal string Path => _directory.FullName;
        internal async Task InitializeAsync()
        {
            await Git("init", "-b", "main");
            await Git("config", "user.name", "Test");
            await Git("config", "user.email", "test@example.com");
            await CommitAsync("base");
        }
        internal Task<string> Git(params string[] arguments) => RunGitAsync(Path, null, arguments);
        internal Task<string> GitWithInput(string input, params string[] arguments) => RunGitAsync(Path, input, arguments);
        internal Task<string> CommitAsync(string message) => Git("commit", "--allow-empty", "-m", message);
        internal Task<string> TagAsync(string name, bool annotated)
            => annotated ? Git("tag", "-a", name, "-m", name) : Git("tag", name);
        internal Task<string?> SelectAsync() => GitVersionDetails.GetLatestStableVersionTagAsync(Path, CancellationToken.None);
        public void Dispose()
        {
            foreach (var file in _directory.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes &= ~FileAttributes.ReadOnly;
            _directory.Delete(recursive: true);
        }
    }

    private static async Task<string> RunGitAsync(string directory, string? input, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardInput = input is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync();
        var outputText = await output;
        var errorText = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException(errorText);
        return outputText.Trim();
    }
}
