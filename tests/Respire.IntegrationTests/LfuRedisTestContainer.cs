namespace Respire.IntegrationTests;

/// <summary>
/// Session-wide Redis server with the LFU eviction policy, so OBJECT FREQ tests never change the
/// shared server's policy. Each test still gets its own database.
/// </summary>
public sealed class LfuRedisTestContainer() : RedisTestContainer("--maxmemory-policy", "allkeys-lfu", "--lfu-decay-time", "0");
