using Respire.Pipeline.Modules;
using Respire.Pipeline.Settings;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class NextVersionTests
{
    [Test]
    [Arguments("v0.13.3", "0.13.4")]
    [Arguments("1.2.3", "1.2.4")]
    [Arguments("v2.0.0-rc.2000", "2.0.0-rc.2001")]
    [Arguments("v2.0.0-rc.9", "2.0.0-rc.10")]
    [Arguments("v2.0.0-rc.0", "2.0.0-rc.1")]
    [Arguments("v2.0.0-beta01", "2.0.0-beta01.1")]
    [Arguments("v2.0.0-beta", "2.0.0-beta.1")]
    [Arguments("v2.0.0-7", "2.0.0-8")]
    [Arguments("v2.0.0-preview.1.rc", "2.0.0-preview.1.rc.1")]
    public async Task NextVersionIsOneStepAboveTheTag(string tag, string expected)
    {
        await Assert.That(GitVersionDetails.GetNextVersion(tag)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("v1.02.3")]
    [Arguments("v01.2.3")]
    [Arguments("v1.2.03")]
    [Arguments("v1.2.3-beta..1")]
    [Arguments("v1.2.3-beta.01")]
    [Arguments("v1.2.3-01")]
    [Arguments("v1.2.3-")]
    [Arguments("v1.2")]
    [Arguments("v65535.0.0")]
    [Arguments("v0.0.65535")]
    [Arguments("v2.0.0+build")]
    [Arguments("v2.0.0-rc.1+build")]
    [Arguments("release-1")]
    public async Task NonVersionTagsHaveNoNextVersion(string tag)
    {
        await Assert.That(GitVersionDetails.GetNextVersion(tag)).IsNull();
    }

    [Test]
    [Arguments("v1.2.3", "1.2.3")]
    [Arguments("1.2.3", "1.2.3")]
    [Arguments("v0.0.0", "0.0.0")]
    [Arguments("v1.2.3-beta.1", "1.2.3-beta.1")]
    [Arguments("v1.2.3-beta01", "1.2.3-beta01")]
    [Arguments("v1.2.3-0", "1.2.3-0")]
    [Arguments("v65534.65534.65534", "65534.65534.65534")]
    public async Task ValidReleaseVersionsAreAccepted(string releaseVersion, string expected)
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await Assert.That((await repository.VersionAsync(releaseVersion: releaseVersion)).PackageVersion).IsEqualTo(expected);
    }

    [Test]
    public async Task PatchAtTheAssemblyVersionLimitHasNoNextVersion()
    {
        await Assert.That(() => GitVersionDetails.GetNextVersion("v1.2.65534")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task MainSuggestsOneStepAboveTheNearestTagRegardlessOfCommitCount()
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await repository.Git("tag", "v2.0.0-rc.2000");

        for (var commit = 0; commit < 3; commit++)
        {
            await repository.Git("commit", "--allow-empty", "-m", "+semver:major");
        }

        var version = await repository.VersionAsync();
        await Assert.That(version.PackageVersion).IsEqualTo("2.0.0-rc.2001");
        await Assert.That(version.BaseVersion).IsEqualTo("v2.0.0-rc.2000");
        await Assert.That(version.CommitHeight).IsEqualTo(3);

        await repository.Git("tag", "v2.0.0");
        await repository.Git("commit", "--allow-empty", "-m", "after the stable cut");
        await Assert.That((await repository.VersionAsync()).PackageVersion).IsEqualTo("2.0.1");
    }

    [Test]
    public async Task UnmergedTagDoesNotChangeMain()
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await repository.Git("tag", "v1.2.3");
        await repository.Git("checkout", "-b", "other");
        await repository.Git("commit", "--allow-empty", "-m", "unmerged release");
        await repository.Git("tag", "v9.0.0");
        await repository.Git("checkout", "main");
        await Assert.That((await repository.VersionAsync()).PackageVersion).IsEqualTo("1.2.4");
    }

    [Test]
    public async Task WithoutTagsTheBaseVersionIsUsed()
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await Assert.That((await repository.VersionAsync()).PackageVersion).IsEqualTo("0.1.0");
    }

    [Test]
    [Arguments("v1.2.3", "1.2.4-ci.feature-test.1.g")]
    [Arguments("v2.0.0-rc.5", "2.0.0-rc.6.ci.feature-test.1.g")]
    public async Task FeatureBranchesGetCiPrereleases(string tag, string expectedPrefix)
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await repository.Git("tag", tag);
        await repository.Git("commit", "--allow-empty", "-m", "feature work");

        var version = await repository.VersionAsync(branch: "feature/test");
        await Assert.That(version.PackageVersion).IsEqualTo($"{expectedPrefix}{version.ShortCommitHash}");
    }

    [Test]
    [Arguments("2.0.0", "2.0.0")]
    [Arguments("2.1.0-rc.1", "2.1.0-rc.1")]
    [Arguments("v3.0.0", "3.0.0")]
    public async Task PublishedReleaseUsesItsTagVersionOnAnyBranch(string releaseVersion, string expected)
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await repository.Git("tag", "v0.13.3");

        var version = await repository.VersionAsync(branch: "v-anything", releaseVersion: releaseVersion);
        await Assert.That(version.PackageVersion).IsEqualTo(expected);
        await Assert.That(version.IncrementKind).IsEqualTo("release");
    }

    [Test]
    [Arguments("")]
    [Arguments("2.0")]
    [Arguments("2.0.0+build")]
    [Arguments("release-1")]
    [Arguments("v1.02.3")]
    [Arguments("v1.2.3-beta.01")]
    [Arguments("v65535.0.0")]
    public async Task InvalidReleaseVersionFails(string releaseVersion)
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        await Assert.That(async () => await repository.VersionAsync(releaseVersion: releaseVersion))
            .Throws<InvalidOperationException>();
    }

    private sealed class Repository : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("respire-next-version-");

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

        internal Task<GitVersionDetails> VersionAsync(string branch = "main", string? releaseVersion = null)
            => GitVersionDetails.CreateAsync(new GitVersioningSettings(),
                _directory.FullName, branch, CancellationToken.None, releaseVersion: releaseVersion);

        public void Dispose()
        {
            foreach (var file in _directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }

            _directory.Delete(recursive: true);
        }
    }
}
