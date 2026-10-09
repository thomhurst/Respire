using Hangfire.Redis.Tests.Utils;
using TUnit.Core;

namespace Hangfire.Redis.Tests;

// Every upstream class is NotInParallel. The original static RedisUtils API also
// serves its explicit worker threads; a test owns this connection until teardown.
public abstract class UpstreamRedisTest
{
    protected UpstreamRedisTest(RedisTestContainer fixture) => RedisUtils.Initialize(fixture);

    [After(Test)]
    public ValueTask DisposeConnection() => RedisUtils.DisposeAsync();
}
