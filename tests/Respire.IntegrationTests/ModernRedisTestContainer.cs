namespace Respire.IntegrationTests;

/// <summary>Session-wide Redis 8 fixture for commands unavailable in the shared Redis 7 fixture.</summary>
public sealed class ModernRedisTestContainer() : StandaloneRedisTestContainer("redis:8-alpine");
