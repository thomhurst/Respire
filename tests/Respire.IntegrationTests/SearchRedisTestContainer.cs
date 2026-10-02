namespace Respire.IntegrationTests;

/// <summary>Redis 8.4 is the first Redis Open Source version supporting FT.HYBRID.</summary>
public sealed class SearchRedisTestContainer() : StandaloneRedisTestContainer("redis:8.4-alpine");
