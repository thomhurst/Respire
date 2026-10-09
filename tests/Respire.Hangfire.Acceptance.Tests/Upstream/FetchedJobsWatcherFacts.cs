#nullable disable
// Adapted from Hangfire.Redis.StackExchange 1.12.0, commit da8e39a33df204900afc30aeb65110f76f081c55.
// Changes: TUnit discovery, isolated Testcontainers fixture, and injected Respire shim.
// See NOTICE.md and License.md for upstream copyright and LGPLv3 terms.
using System;
using System.Threading;
using Hangfire.Common;
using Hangfire.Redis.StackExchange;
using Hangfire.Redis.Tests.Utils;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Hangfire.Redis.Tests
{
    [ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
    [NotInParallel]
    public class FetchedJobsWatcherFacts : UpstreamRedisTest
    {
        private static readonly TimeSpan InvisibilityTimeout = TimeSpan.FromSeconds(10);

        private readonly RedisStorage _storage;
        private readonly CancellationTokenSource _cts;

        [After(Test)]
        public void DisposeCancellation() => _cts.Dispose();

        public FetchedJobsWatcherFacts(RedisTestContainer fixture) : base(fixture)
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
                () => new FetchedJobsWatcher(null, InvisibilityTimeout));
        }

        [Test]
        public void Ctor_ThrowsAnException_WhenInvisibilityTimeoutIsZero()
        {
            Assert.Throws<ArgumentOutOfRangeException>("invisibilityTimeout",
                () => new FetchedJobsWatcher(_storage, TimeSpan.Zero));
        }

        [Test]
        public void Ctor_ThrowsAnException_WhenInvisibilityTimeoutIsNegative()
        {
            Assert.Throws<ArgumentOutOfRangeException>("invisibilityTimeout",
                () => new FetchedJobsWatcher(_storage, TimeSpan.FromSeconds(-1)));
        }

        [Test]
        public void Execute_EnqueuesTimedOutJobs_AndDeletesThemFromFetchedList()
        {
            var redis = RedisUtils.CreateClient();
            // Arrange
            redis.SetAdd("{hangfire}:queues", "my-queue");
            redis.ListRightPush("{hangfire}:queue:my-queue:dequeued", "my-job");
            redis.HashSet("{hangfire}:job:my-job", "Fetched",
                JobHelper.SerializeDateTime(DateTime.UtcNow.AddDays(-1)));

            var watcher = CreateWatcher();

            // Act
            watcher.Execute(_cts.Token);

            // Assert
            Assert.Equal(0, redis.ListLength("{hangfire}:queue:my-queue:dequeued"));

            var listEntry = (string) redis.ListRightPop("{hangfire}:queue:my-queue");
            Assert.Equal("my-job", listEntry);

            var job = redis.HashGetAll("{hangfire}:job:my-job");
            Assert.DoesNotContain(job, x => x.Name == "Fetched");
        }

        [Test]
        public void Execute_MarksDequeuedJobAsChecked_IfItHasNoFetchedFlagSet()
        {
            var redis = RedisUtils.CreateClient();
            // Arrange
            redis.SetAdd("{hangfire}:queues", "my-queue");
            redis.ListRightPush("{hangfire}:queue:my-queue:dequeued", "my-job");

            var watcher = CreateWatcher();

            // Act
            watcher.Execute(_cts.Token);

            Assert.NotNull(JobHelper.DeserializeNullableDateTime(
                redis.HashGet("{hangfire}:job:my-job", "Checked")));
        }

        [Test]
        public void Execute_EnqueuesCheckedAndTimedOutJob_IfNoFetchedFlagSet()
        {
            var redis = RedisUtils.CreateClient();
            // Arrange
            redis.SetAdd("{hangfire}:queues", "my-queue");
            redis.ListRightPush("{hangfire}:queue:my-queue:dequeued", "my-job");
            redis.HashSet("{hangfire}:job:my-job", "Checked",
                JobHelper.SerializeDateTime(DateTime.UtcNow.AddDays(-1)));

            var watcher = CreateWatcher();

            // Act
            watcher.Execute(_cts.Token);

            // Arrange
            Assert.Equal(0, redis.ListLength("{hangfire}:queue:my-queue:dequeued"));
            Assert.Equal(1, redis.ListLength("{hangfire}:queue:my-queue"));

            var job = redis.HashGetAll("{hangfire}:job:my-job");
            Assert.DoesNotContain(job, x => x.Name == "Checked");
        }

        [Test]
        public void Execute_DoesNotEnqueueTimedOutByCheckedFlagJob_IfFetchedFlagSet()
        {
            var redis = RedisUtils.CreateClient();

            // Arrange
            redis.SetAdd("{hangfire}:queues", "my-queue");
            redis.ListRightPush("{hangfire}:queue:my-queue:dequeued", "my-job");
            redis.HashSet("{hangfire}:job:my-job", "Checked",
                JobHelper.SerializeDateTime(DateTime.UtcNow.AddDays(-1)));
            redis.HashSet("{hangfire}:job:my-job", "Fetched",
                JobHelper.SerializeDateTime(DateTime.UtcNow));

            var watcher = CreateWatcher();

            // Act
            watcher.Execute(_cts.Token);

            // Assert
            Assert.Equal(1, redis.ListLength("{hangfire}:queue:my-queue:dequeued"));
        }

        private FetchedJobsWatcher CreateWatcher()
        {
            return new FetchedJobsWatcher(_storage, InvisibilityTimeout);
        }
    }
}