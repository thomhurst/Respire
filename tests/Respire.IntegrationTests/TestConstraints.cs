using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

/// <summary>Keys shared by fixtures and <c>NotInParallel</c> constraints across test classes.</summary>
public static class TestConstraints
{
    /// <summary>
    /// Fixture key for the dedicated servers used by exact client-side cache hit-count tests.
    /// Load from other tests on the shared servers could delay tracking replies enough to trigger a
    /// continuity flush, which legitimately turns an expected local hit into a server miss.
    /// </summary>
    public const string ClientCacheServer = "client-cache";

    /// <summary>
    /// Serializes the exact hit-count tests on <see cref="ClientCacheServer"/> with each other only.
    /// </summary>
    public const string ClientCacheHits = "client-cache-hits";
}

/// <summary>Categories used to select subsets of the suite in CI.</summary>
public static class TestCategories
{
    /// <summary>
    /// Tests that never read RESPIRE_TEST_PROTOCOL: they own their server, use a fixture other than
    /// <c>RedisTestContainer</c>, or pin the protocol themselves. The RESP2 CI job excludes them with
    /// <c>--treenode-filter "/*/*/*/*[Category!=ProtocolIndependent]"</c>; untagged tests always run there.
    /// </summary>
    public const string ProtocolIndependent = "ProtocolIndependent";
}

/// <summary>
/// Caps concurrent tests that start their own multi-node topology (cluster, Sentinel, replicas),
/// which each run several server processes and are sensitive to timing under heavy load.
/// </summary>
public sealed class DockerHeavy : IParallelLimit
{
    public int Limit => 3;
}
