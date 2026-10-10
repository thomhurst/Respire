#nullable disable
// Adapted from Hangfire.Redis.StackExchange 1.12.0, commit da8e39a33df204900afc30aeb65110f76f081c55.
// Changes: TUnit discovery, isolated Testcontainers fixture, and injected Respire shim.
// See NOTICE.md and License.md for upstream copyright and LGPLv3 terms.
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using Hangfire.Redis.StackExchange;
using Hangfire.Redis.Tests.Utils;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Hangfire.Redis.Tests
{
    [ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
    [NotInParallel]
    public class RedisConnectionFacts : UpstreamRedisTest
    {
        private readonly RedisStorage _storage;

        public RedisConnectionFacts(RedisTestContainer fixture) : base(fixture)
        {
            var options = new RedisStorageOptions() {Db = RedisUtils.GetDb()};
            _storage = new RedisStorage(RedisUtils.Connection, options);
        }

        [Test]
        public void GetStateData_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(
                connection => Assert.Throws<ArgumentNullException>("jobId",
                    () => connection.GetStateData(null)));
        }

        [Test]
        public void GetStateData_ReturnsNull_WhenJobDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetStateData("random-id");
                Assert.Null(result);
            });
        }

        [Test]
        public void GetStateData_ReturnsCorrectResult()
        {
            UseConnections((redis, connection) =>
            {
                redis.HashSet(
                    "{hangfire}:job:my-job:state",
                    new Dictionary<string, string>
                    {
                        {"State", "Name"},
                        {"Reason", "Reason"},
                        {"Key", "Value"}
                    }.ToHashEntries());

                var result = connection.GetStateData("my-job");

                Assert.NotNull(result);
                Assert.Equal("Name", result.Name);
                Assert.Equal("Reason", result.Reason);
                Assert.Equal("Value", result.Data["Key"]);
            });
        }

        [Test]
        public void GetStateData_ReturnsNullReason_IfThereIsNoSuchKey()
        {
            UseConnections((redis, connection) =>
            {
                redis.HashSet(
                    "{hangfire}:job:my-job:state",
                    new Dictionary<string, string>
                    {
                        {"State", "Name"}
                    }.ToHashEntries());

                var result = connection.GetStateData("my-job");

                Assert.NotNull(result);
                Assert.Null(result.Reason);
            });
        }

        [Test]
        public void GetAllItemsFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
                Assert.Throws<ArgumentNullException>("key",
                    () => connection.GetAllItemsFromSet(null)));
        }

        [Test]
        public void GetAllItemsFromSet_ReturnsEmptyCollection_WhenSetDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetAllItemsFromSet("some-set");

                Assert.NotNull(result);
                Assert.Empty(result);
            });
        }

        [Test]
        public void GetAllItemsFromSet_ReturnsAllItems()
        {
            UseConnections((redis, connection) =>
            {
                // Arrange
                redis.SortedSetAdd("{hangfire}:some-set", "1", 0);
                redis.SortedSetAdd("{hangfire}:some-set", "2", 0);

                // Act
                var result = connection.GetAllItemsFromSet("some-set");

                // Assert
                Assert.Equal(2, result.Count);
                Assert.Contains("1", result);
                Assert.Contains("2", result);
            });
        }

        [Test]
        public void SetRangeInHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>("key",
                    () => connection.SetRangeInHash(null, new Dictionary<string, string>()));
            });
        }

        [Test]
        public void SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>("keyValuePairs",
                    () => connection.SetRangeInHash("some-hash", null));
            });
        }

        [Test]
        public void SetRangeInHash_SetsAllGivenKeyPairs()
        {
            UseConnections((redis, connection) =>
            {
                connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    {"Key1", "Value1"},
                    {"Key2", "Value2"}
                });

                var hash = redis.HashGetAll("{hangfire}:some-hash").ToStringDictionary();
                Assert.Equal("Value1", hash["Key1"]);
                Assert.Equal("Value2", hash["Key2"]);
            });
        }

        [Test]
        public void GetAllEntriesFromHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
                Assert.Throws<ArgumentNullException>(() => connection.GetAllEntriesFromHash(null)));
        }

        [Test]
        public void GetAllEntriesFromHash_ReturnsNullValue_WhenHashDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetAllEntriesFromHash("some-hash");
                Assert.Null(result);
            });
        }

        [Test]
        public void GetAllEntriesFromHash_ReturnsAllEntries()
        {
            UseConnections((redis, connection) =>
            {
                // Arrange
                redis.HashSet("{hangfire}:some-hash", new Dictionary<string, string>
                {
                    {"Key1", "Value1"},
                    {"Key2", "Value2"}
                }.ToHashEntries());

                // Act
                var result = connection.GetAllEntriesFromHash("some-hash");

                // Assert
                Assert.NotNull(result);
                Assert.Equal("Value1", result["Key1"]);
                Assert.Equal("Value2", result["Key2"]);
            });
        }

        [Test]
        public void GetUtcDateTime_ReturnsValidDateTime()
        {
            UseConnections((redis, connection) =>
            {
                var result = connection.GetUtcDateTime();

                Assert.NotEqual(default, result);
            });
        }

        [Test]
        public void SetCount_ReturnZeroIfSetDoesNotExists()
        {
            UseConnections((redis, connection) =>
            {
                var result = connection.GetSetCount("some-set");

                Assert.Equal(0, result);
            });
        }

        [Test]
        public void SetCount_ReturnNumberOfItems()
        {
            UseConnections((redis, connection) =>
            {
                redis.SortedSetAdd("{hangfire}:some-set", "1", 0);
                redis.SortedSetAdd("{hangfire}:some-set", "2", 0);

                var result = connection.GetSetCount("some-set");

                Assert.Equal(2, result);
            });
        }

        [Test]
        public void SetContains_ReturnTrueIfContained()
        {
            UseConnections((redis, connection) =>
            {
                redis.SortedSetAdd("{hangfire}:some-set", "1", 0);

                var result = connection.GetSetContains("some-set", "1");

                Assert.True(result);
            });
        }

        [Test]
        public void SetContains_ReturnFalseIfNotContained()
        {
            UseConnections((redis, connection) =>
            {
                redis.SortedSetAdd("{hangfire}:some-set", "1", 0);

                var result = connection.GetSetContains("some-set", "0");

                Assert.False(result);
            });
        }

        private void UseConnections(Action<IDatabase, RedisConnection> action)
        {
            var redis = RedisUtils.CreateClient();
            var subscription = new RedisSubscription(_storage, RedisUtils.CreateSubscriber());
            var server = RedisUtils.GetFirstServer();
            using (var connection = new RedisConnection(_storage, server, RedisUtils.ShimDatabase, subscription, new RedisStorageOptions().FetchTimeout))
            {
                action(redis, connection);
            }
        }

        private void UseConnection(Action<RedisConnection> action)
        {
            var redis = RedisUtils.CreateClient();
            var subscription = new RedisSubscription(_storage, RedisUtils.CreateSubscriber());
            var server = RedisUtils.GetFirstServer();

            using (var connection = new RedisConnection(_storage, server, RedisUtils.ShimDatabase, subscription, new RedisStorageOptions().FetchTimeout))
            {
                action(connection);
            }
        }

    }
}