using ModularPipelines;
using Respire.Pipeline.Modules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class UploadPackagesToNugetModuleTests
{
    [Test]
    public async Task PublishedReleaseUploads()
    {
        await Assert.That(UploadPackagesToNugetModule.GetSkipDecision("release").ShouldSkip).IsFalse();
    }

    [Test]
    [Arguments("push")]
    [Arguments("pull_request")]
    [Arguments("workflow_dispatch")]
    [Arguments(null)]
    public async Task EveryOtherRunSkipsTheUpload(string? eventName)
    {
        await Assert.That(UploadPackagesToNugetModule.GetSkipDecision(eventName).ShouldSkip).IsTrue();
    }
}
