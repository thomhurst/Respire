using Respire.Pipeline.Modules;
using Respire.Pipeline.Settings;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class ReleaseVersionTests
{
    [Test]
    public async Task ReleaseCandidatesIgnoreStrayTagsAndSemverMarkers()
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await repository.Git("tag", "v0.8.0");
        await repository.Git("tag", "v1.0.116");
        await repository.Git("commit", "--allow-empty", "-m", "+semver:major");
        await repository.Git("tag", "v99.0.0");

        var first = await repository.VersionAsync();
        await Assert.That(first.PackageVersion).IsEqualTo("2.0.0-rc.2");
        await Assert.That(first.BaseVersion).IsEqualTo("2.0.0");
        await Assert.That(first.IncrementKind).IsEqualTo("rc");
        await repository.Git("tag", "v2.0.0-rc.2");
        await repository.Git("commit", "--allow-empty", "-m", "next candidate");
        await Assert.That((await repository.VersionAsync()).PackageVersion).IsEqualTo("2.0.0-rc.3");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitStableTagPromotesReleaseAndStartsPatchHeight(bool annotated)
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        if (annotated) await repository.Git("tag", "-a", "v2.0.0", "-m", "stable cut");
        else await repository.Git("tag", "v2.0.0");

        await Assert.That((await repository.VersionAsync()).PackageVersion).IsEqualTo("2.0.0");
        await repository.Git("commit", "--allow-empty", "-m", "+semver:major");
        await repository.Git("tag", "v99.0.0");
        await Assert.That((await repository.VersionAsync()).PackageVersion).IsEqualTo("2.0.1");
        // A new explicit target starts its own candidate phase.
        await Assert.That((await repository.VersionAsync(target: "2.1.0")).PackageVersion)
            .IsEqualTo("2.1.0-rc.2");
    }

    [Test]
    public async Task StableTagOnUnmergedBranchDoesNotPromoteMain()
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await repository.Git("checkout", "-b", "other");
        await repository.Git("commit", "--allow-empty", "-m", "unmerged release");
        await repository.Git("tag", "v2.0.0");
        await repository.Git("checkout", "main");
        await Assert.That((await repository.VersionAsync()).PackageVersion).IsEqualTo("2.0.0-rc.1");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FeatureBranchesStayCiEvenAtTheStableTag(bool released)
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        if (released) await repository.Git("tag", "v2.0.0");
        var version = await repository.VersionAsync(branch: "feature/test");
        var height = released ? 0 : 1;
        await Assert.That(version.PackageVersion).IsEqualTo($"2.0.0-ci.feature-test.{height}.g{version.ShortCommitHash}");
    }

    [Test]
    [Arguments("")]
    [Arguments("v2.0.0")]
    [Arguments("02.0.0")]
    [Arguments("2.0")]
    [Arguments("2.0.0-rc.1")]
    [Arguments("2.0.0+build")]
    public async Task InvalidExplicitTargetFailsInsteadOfFallingBackToTags(string target)
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await Assert.That(async () => await repository.VersionAsync(target: target))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task UnconfiguredTargetRetainsLegacyVersioning()
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await repository.Git("tag", "v0.8.0");
        await repository.Git("commit", "--allow-empty", "-m", "patch");
        await Assert.That((await repository.VersionAsync(target: null)).PackageVersion).IsEqualTo("0.8.1");
    }

    private sealed class Repository : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("respire-release-version-");

        internal async Task InitializeAsync()
        {
            await Git("init", "-b", "main");
            await Git("config", "user.name", "Test");
            await Git("config", "user.email", "test@example.com");
            await Git("-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "base");
            await Git("config", "commit.gpgsign", "false");
            await Git("config", "tag.gpgsign", "false");
        }

        internal Task<string> Git(params string[] arguments)
            => GitCommand.RunAsync(_directory.FullName, CancellationToken.None, arguments);

        internal Task<GitVersionDetails> VersionAsync(string branch = "main", string? target = "2.0.0")
            => GitVersionDetails.CreateAsync(new GitVersioningSettings { ReleaseVersion = target },
                _directory.FullName, branch, CancellationToken.None);

        public void Dispose()
        {
            foreach (var file in _directory.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes &= ~FileAttributes.ReadOnly;
            _directory.Delete(recursive: true);
        }
    }
}
