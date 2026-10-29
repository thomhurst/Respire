namespace Respire.IntegrationTests;

/// <summary>
/// Pins tests of Redis-specific semantics and legacy unsupported commands to their reference version,
/// even when the general compatibility suite selects Valkey through RESPIRE_TEST_REDIS_IMAGE.
/// </summary>
public sealed class Redis7ReferenceContainer : RedisTestContainer
{
    protected override string ServerImage => ReferenceRedisImage;
}
