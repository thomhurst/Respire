using Respire.Pipeline.Modules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class VersionIncrementResultTests
{
    [Test]
    public async Task MinorMarkerAtHead_ResetsPatchHeightToZero()
    {
        var result = VersionIncrementResult.FromCommitMessages(
            CommitLog("Add git-based NuGet versioning", "+semver:minor - Merge pull request #78"),
            commitHeight: 2);

        await Assert.That(result.Increment).IsEqualTo(VersionIncrement.Minor);
        await Assert.That(result.PatchHeight).IsEqualTo(0);
    }

    [Test]
    public async Task MinorMarker_CountsCommitsAfterMarkerAsPatchHeight()
    {
        var result = VersionIncrementResult.FromCommitMessages(
            CommitLog("+semver:minor - Merge feature", "fix: follow-up", "docs: update readme"),
            commitHeight: 3);

        await Assert.That(result.Increment).IsEqualTo(VersionIncrement.Minor);
        await Assert.That(result.PatchHeight).IsEqualTo(2);
    }

    [Test]
    public async Task NoMarker_UsesCommitHeightForPatchIncrement()
    {
        var result = VersionIncrementResult.FromCommitMessages(
            CommitLog("fix: follow-up", "docs: update readme"),
            commitHeight: 2);

        await Assert.That(result.Increment).IsEqualTo(VersionIncrement.Patch);
        await Assert.That(result.PatchHeight).IsEqualTo(2);
    }

    private static string CommitLog(params string[] messages)
    {
        return string.Join('\x1e', messages) + '\x1e';
    }
}
