#nullable disable
// Adapted from Hangfire.Redis.StackExchange 1.12.0, commit da8e39a33df204900afc30aeb65110f76f081c55.
// Changes: TUnit discovery, isolated Testcontainers fixture, and injected Respire shim.
// See NOTICE.md and License.md for upstream copyright and LGPLv3 terms.
using System;
using System.Threading;
using Hangfire.Common;
using Hangfire.Redis.StackExchange;
using Hangfire.Redis.Tests.Utils;
using Hangfire.Server;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Hangfire.Redis.Tests
{
    [ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
    [NotInParallel]
    public class ExpiredJobsWatcherFacts : UpstreamRedisTest
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

        private readonly RedisStorage _storage;
        private readonly CancellationTokenSource _cts;

        [After(Test)]
        public void DisposeCancellation() => _cts.Dispose();

        public ExpiredJobsWatcherFacts(RedisTestContainer fixture) : base(fixture)
        {
            var options = new RedisStorageOptions() {Db = RedisUtils.GetDb()};
            _storage = new RedisStorage(RedisUtils.Connection, options);
            _cts = new CancellationTokenSource();
            _cts.Cancel();
        }

        [Test]
        public void Ctor_ThrowsAnException_WhenStorageIsNull()
        {
            Assert.Throws<ArgumentNullException>("storage",
                () => new ExpiredJobsWatcher(null, CheckInterval));
        }

        [Test]
        public void Ctor_ThrowsAnException_WhenCheckIntervalIsZero()
        {
            Assert.Throws<ArgumentOutOfRangeException>("checkInterval",
                () => new ExpiredJobsWatcher(_storage, TimeSpan.Zero));
        }

        [Test]
        public void Ctor_ThrowsAnException_WhenCheckIntervalIsNegative()
        {
            Assert.Throws<ArgumentOutOfRangeException>("checkInterval",
                () => new ExpiredJobsWatcher(_storage, TimeSpan.FromSeconds(-1)));
        }

        [Test]
        public void Execute_DeletesNonExistingJobs()
        {
            var redis = RedisUtils.CreateClient();

            Assert.Equal(0, redis.ListLength("{hangfire}:succeeded"));
            Assert.Equal(0, redis.ListLength("{hangfire}:deleted"));

            // Arrange
            redis.ListRightPush("{hangfire}:succeded", "my-job");
            redis.ListRightPush("{hangfire}:deleted", "other-job");

            var watcher = CreateWatcher();

            // Act
            watcher.Execute(_cts.Token);

            // Assert
            Assert.Equal(0, redis.ListLength("{hangfire}:succeeded"));
            Assert.Equal(0, redis.ListLength("{hangfire}:deleted"));
        }

        [Test]
        public void Execute_DoesNotDeleteExistingJobs()
        {
            var redis = RedisUtils.CreateClient();
            // Arrange
            redis.ListRightPush("{hangfire}:succeeded", "my-job");
            redis.HashSet("{hangfire}:job:my-job", "Fetched",
                JobHelper.SerializeDateTime(DateTime.UtcNow.AddDays(-1)));

            redis.ListRightPush("{hangfire}:deleted", "other-job");
            redis.HashSet("{hangfire}:job:other-job", "Fetched",
                JobHelper.SerializeDateTime(DateTime.UtcNow.AddDays(-1)));

            var watcher = CreateWatcher();

            // Act
            watcher.Execute(_cts.Token);

            // Assert
            Assert.Equal(1, redis.ListLength("{hangfire}:succeeded"));
            Assert.Equal(1, redis.ListLength("{hangfire}:deleted"));
        }

#pragma warning disable 618
        private IServerComponent CreateWatcher()
#pragma warning restore 618
        {
            return new ExpiredJobsWatcher(_storage, CheckInterval);
        }
    }
}