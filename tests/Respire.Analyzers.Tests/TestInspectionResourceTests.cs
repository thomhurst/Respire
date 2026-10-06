using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static Respire.Analyzers.Tests.TestInspectionSource;

namespace Respire.Analyzers.Tests;

public class TestInspectionResourceTests
{
    [Test]
    public async Task EmbeddedProductionSourcesExcludeFriendTestsAndBuildOutput()
    {
        var sources = ReadLibrarySources().ToArray();
        await Assert.That(sources).IsNotEmpty();
        await Assert.That(sources.Any(source => source.Path.Split('/')
            .Any(segment => segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))).IsFalse();
        await Assert.That(sources.Any(source => source.Path.EndsWith("/RespireConnection.TestInspection.cs", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task MissingResourceReportsItsName()
    {
        const string missing = "MissingTestInspectionOwnerSurface.txt";
        InvalidOperationException? failure = null;
        try { using var stream = OpenResource(missing); }
        catch (InvalidOperationException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).IsEqualTo("Missing embedded resource: " + missing);
    }
}
