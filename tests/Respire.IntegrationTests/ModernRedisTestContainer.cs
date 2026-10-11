namespace Respire.IntegrationTests;

/// <summary>
/// Session-wide Redis 8.10 fixture for commands and modules unavailable in the shared Redis 7 fixture.
/// It covers the newest feature floors in the suite: FT.HYBRID (8.4), XADD IDMP (8.6), and the
/// 8.10 JSON and TimeSeries reads. Tests share database 0, so every key must be unique to its test.
/// ListMoveManyIntegrationTests owns databases 2 and 3 for its fixed-key WATCH parity scenarios.
/// </summary>
public sealed class ModernRedisTestContainer() : StandaloneRedisTestContainer("redis:8.10-alpine");
