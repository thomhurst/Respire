using ModularPipelines;
using Respire.Pipeline.Modules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class CreateGitHubReleaseModuleTests
{
    [Test]
    [Arguments("2.0.0")]
    [Arguments("2.0.0-rc.1935")]
    public async Task CreateReleaseBody_InstallsTemplatesWithoutAddingAProjectDependency(string version)
    {
        var releaseBody = CreateGitHubReleaseModule.CreateReleaseBody(
            "## What's Changed",
            version,
            new[]
            {
                new FileInfo($"Respire.{version}.nupkg"),
                new FileInfo($"Respire.Templates.{version}.nupkg")
            });

        await Assert.That(releaseBody).Contains($"dotnet new install Respire.Templates@{version}");
        await Assert.That(releaseBody).DoesNotContain("dotnet add package Respire.Templates");
        await Assert.That(releaseBody).Contains($"dotnet add package Respire --version {version}");
    }

    [Test]
    public async Task CreateReleaseTag_PrefixesVersionWithV()
    {
        await Assert.That(CreateGitHubReleaseModule.CreateReleaseTag("1.2.3")).IsEqualTo("v1.2.3");
    }

    [Test]
    public async Task CreateReleaseTag_DoesNotDoublePrefixVersion()
    {
        await Assert.That(CreateGitHubReleaseModule.CreateReleaseTag("v1.2.3")).IsEqualTo("v1.2.3");
    }

    [Test]
    public async Task IsPrerelease_ReturnsTrueForPrereleaseVersion()
    {
        await Assert.That(CreateGitHubReleaseModule.IsPrerelease("1.2.3-ci.main.4.abc12345")).IsTrue();
    }

    [Test]
    public async Task CreateReleaseBody_AddsNuGetInstallCommandsForUploadedPackages()
    {
        var releaseBody = CreateGitHubReleaseModule.CreateReleaseBody(
            "## What's Changed",
            "1.2.3",
            new[]
            {
                new FileInfo("Respire.1.2.3.nupkg"),
                new FileInfo("Respire.Caching.1.2.3.nupkg")
            });

        await Assert.That(releaseBody).Contains("## What's Changed");
        await Assert.That(releaseBody).Contains("dotnet add package Respire --version 1.2.3");
        await Assert.That(releaseBody).Contains("dotnet add package Respire.Caching --version 1.2.3");
    }

    [Test]
    public async Task GetSkipDecision_RunsWhenPackagesWereUploaded()
    {
        var decision = CreateGitHubReleaseModule.GetSkipDecision(
            Succeeded([CommandResult.Ok()]));

        await Assert.That(decision.ShouldSkip).IsFalse();
    }

    [Test]
    public async Task GetSkipDecision_SkipsWhenUploadWasSkipped()
    {
        var decision = CreateGitHubReleaseModule.GetSkipDecision(
            new ModuleResult<CommandResult[]>.Skipped(SkipDecision.Skip("not a release build"))
            {
                Name = nameof(UploadPackagesToNugetModule),
                Duration = TimeSpan.Zero,
                StartTime = DateTimeOffset.UnixEpoch,
                EndTime = DateTimeOffset.UnixEpoch,
                Status = ModuleStatus.Skipped
            });

        await Assert.That(decision.ShouldSkip).IsTrue();
        await Assert.That(decision.Reason).IsEqualTo("UploadPackagesToNugetModule was skipped");
    }

    [Test]
    public async Task GetSkipDecision_SkipsWhenNoPackagesWereUploaded()
    {
        var decision = CreateGitHubReleaseModule.GetSkipDecision(
            Succeeded([]));

        await Assert.That(decision.ShouldSkip).IsTrue();
        await Assert.That(decision.Reason).IsEqualTo("No packages were uploaded to NuGet");
    }

    private static ModuleResult<CommandResult[]> Succeeded(CommandResult[] value) =>
        new ModuleResult<CommandResult[]>.Success(value)
        {
            Name = nameof(UploadPackagesToNugetModule),
            Duration = TimeSpan.Zero,
            StartTime = DateTimeOffset.UnixEpoch,
            EndTime = DateTimeOffset.UnixEpoch,
            Status = ModuleStatus.Succeeded
        };
}
