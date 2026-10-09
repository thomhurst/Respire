#nullable disable
// Adapted from Hangfire.Redis.StackExchange 1.12.0, commit da8e39a33df204900afc30aeb65110f76f081c55.
// Changes: TUnit discovery, isolated Testcontainers fixture, and injected Respire shim.
// See NOTICE.md and License.md for upstream copyright and LGPLv3 terms.
using Hangfire.Redis.Tests.Utils;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Hangfire.Redis.Tests
{
    [ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
    [NotInParallel]
    public class RedisTest : UpstreamRedisTest
    {
        private readonly IDatabase _redis;

        public RedisTest(RedisTestContainer fixture) : base(fixture)
        {
            _redis = RedisUtils.ShimDatabase;
        }


        [Test]
        public void RedisSampleTest()
        {
            var defaultValue = _redis.StringGet("samplekey");
            Assert.True(defaultValue.IsNull);
        }
    }
}