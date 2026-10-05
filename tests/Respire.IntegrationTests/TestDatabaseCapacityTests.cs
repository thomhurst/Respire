using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

/// <summary>
/// RedisTestContainer gives each test the logical database numbered by its TUnit isolation ID, and
/// TUnit numbers every test it builds. This fails well before the suite outgrows the database range.
/// </summary>
[Category(TestCategories.ProtocolIndependent)]
public class TestDatabaseCapacityTests
{
    // Leaves room for retries and newly added tests between this warning and a hard failure.
    private const int MaximumTests = 3500;

    [Test]
    public void DiscoveredTestsLeaveRedisDatabaseHeadroom()
    {
        var discovered = DiscoveredTestCount.Value;
        RedisTestContainer.DatabaseCount.Should().BeGreaterThan(MaximumTests);
        discovered.Should().BePositive();
        discovered.Should().BeLessThan(MaximumTests,
            "each test gets its own database out of {0}; split the project or raise the database count",
            RedisTestContainer.DatabaseCount);
        TestContext.Current!.Isolation.UniqueId.Should().BeLessThan(RedisTestContainer.DatabaseCount);
    }
}

internal static class DiscoveredTestCount
{
    public static int Value { get; private set; }

    [After(TestDiscovery)]
    public static void Capture(TestDiscoveryContext context) => Value = context.AllTests.Count();
}
