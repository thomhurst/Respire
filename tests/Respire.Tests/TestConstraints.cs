namespace Respire.Tests;

// Keys for [NotInParallel] tests that need exclusion only from each other. Keyed tests run beside
// the ordinary parallel tests; unkeyed [NotInParallel] tests still run alone after them.
internal static class TestConstraints
{
    /// <summary>
    /// Tests that set <see cref="RespireMetrics"/> configuration only to pin a dispatch path, and assert no
    /// configuration-selected measurement. Ordinary tests listen only to Respire-specific instruments,
    /// which configuration does not select. A test that asserts selected measurements, such as
    /// redis.client.errors, runs alone: final errors carry no server.port to filter on.
    /// </summary>
    internal const string MetricConfiguration = "RespireMetricConfiguration";

    /// <summary>
    /// Tests whose work runs in a child process that shares no state with the test runner. Probes run
    /// one at a time so that their process startups and bounded deadlines do not contend with each other.
    /// </summary>
    internal const string IsolatedProbe = "IsolatedProcessProbe";
}
