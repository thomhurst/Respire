using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static Respire.Analyzers.Tests.TestInspectionSource;

namespace Respire.Analyzers.Tests;

public class TestInspectionResourceTests
{
    /// <summary>Checks the embedded build manifest contains distinct, nonempty SDK framework configurations.</summary>
    [Test]
    public async Task EmbeddedConfigurationsHaveDistinctFrameworksAndSdkSymbols()
    {
        var configurations = ReadSourceConfigurations();
        await Assert.That(configurations).IsNotEmpty();
        await Assert.That(configurations.Select(configuration => configuration.Framework).Distinct().Count()).IsEqualTo(configurations.Length);
        foreach (var configuration in configurations)
        {
            await Assert.That(configuration.Framework).IsNotEmpty();
            await Assert.That(configuration.Symbols).Contains("NET");
            await Assert.That(configuration.Symbols).Contains("NETCOREAPP");
        }
    }

    /// <summary>Checks source embedding includes production inspection partials while excluding tests and generated output.</summary>
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

    /// <summary>Checks a missing resource produces a useful named failure instead of a null dereference.</summary>
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
