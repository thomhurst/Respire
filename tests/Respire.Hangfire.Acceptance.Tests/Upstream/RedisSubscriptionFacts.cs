#nullable disable
// Adapted from Hangfire.Redis.StackExchange 1.12.0, commit da8e39a33df204900afc30aeb65110f76f081c55.
// Changes: TUnit discovery, isolated Testcontainers fixture, injected Respire shim, and scheduling-tolerant timeout assertion.
// See NOTICE.md and License.md for upstream copyright and LGPLv3 terms.
using Moq;
using StackExchange.Redis;
using System;
using System.Diagnostics;
using System.Threading;
using Hangfire.Redis.StackExchange;
using Hangfire.Redis.Tests.Utils;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Hangfire.Redis.Tests
{
    [ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
    [NotInParallel]
    public class RedisSubscriptionFacts : UpstreamRedisTest
    {
        private readonly CancellationTokenSource _cts;

        [After(Test)]
        public void DisposeCancellation() => _cts.Dispose();
        private readonly RedisStorage _storage;
        private readonly Mock<ISubscriber> _subscriber;

        public RedisSubscriptionFacts(RedisTestContainer fixture) : base(fixture)
        {
            _cts = new CancellationTokenSource();

            var options = new RedisStorageOptions() { Db = RedisUtils.GetDb() };
            _storage = new RedisStorage(RedisUtils.Connection, options);

            _subscriber = new Mock<ISubscriber>();
        }

        [Test]
        public void Ctor_ThrowAnException_WhenStorageIsNull()
        {
            Assert.Throws<ArgumentNullException>("storage",
                () => new RedisSubscription(null, _subscriber.Object));
        }

        [Test]
        public void Ctor_ThrowAnException_WhenSubscriberIsNull()
        {
            Assert.Throws<ArgumentNullException>("subscriber",
                () => new RedisSubscription(_storage, null));
        }
        [Test]
        public void WaitForJob_WaitForTheTimeout()
        {
            //Arrange
            Stopwatch sw = new Stopwatch();
            var subscription = new RedisSubscription(_storage, RedisUtils.CreateSubscriber());
            var timeout = TimeSpan.FromMilliseconds(100);
            sw.Start();

            //Act
            subscription.WaitForJob(timeout, _cts.Token);

            //Assert
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds >= 99);
        }
    }

}
