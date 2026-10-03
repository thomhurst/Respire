using Respire.Pipeline.Modules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class PrereleaseVersionTests
{
    [Test]
    [Arguments("issue-738-topology-snapshot-20261003", "01800081", "issue-738-topology-snapshot-20261003", "g01800081")]
    [Arguments("feature/Read-Routing", "abcdef12", "feature-read-routing", "gabcdef12")]
    [Arguments("001", "00000000", "branch-001", "g00000000")]
    [Arguments("123", "12345678", "branch-123", "g12345678")]
    [Arguments("///", "deadbeef", "branch", "gdeadbeef")]
    public async Task GitIdentifiersRemainValidTextualPrereleaseComponents(
        string branch, string hash, string expectedBranch, string expectedHash)
    {
        var version = GitVersionDetails.CreatePrereleaseVersion("0.6.300", branch, 299, hash);
        await Assert.That(version).IsEqualTo($"0.6.300-ci.{expectedBranch}.299.{expectedHash}");
    }
}
