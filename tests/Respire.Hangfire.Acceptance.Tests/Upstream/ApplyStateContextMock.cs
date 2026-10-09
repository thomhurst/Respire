#nullable disable
// Adapted from Hangfire.Redis.StackExchange 1.12.0, commit da8e39a33df204900afc30aeb65110f76f081c55.
// Changes: TUnit discovery, isolated Testcontainers fixture, and injected Respire shim.
// See NOTICE.md and License.md for upstream copyright and LGPLv3 terms.
using System;
using Hangfire.States;
using Moq;
using Hangfire.Common;
using Hangfire.Redis.StackExchange;
using Hangfire.Redis.Tests.Utils;

namespace Hangfire.Redis.Tests
{
    public class ApplyStateContextMock
    {
        private readonly Lazy<ApplyStateContext> _context;

        public ApplyStateContextMock(string jobId)
        {
            NewStateValue = new Mock<IState>().Object;
            OldStateValue = null;
            var storage = CreateStorage();
            var connection = storage.GetConnection();
            var writeOnlyTransaction = connection.CreateWriteTransaction();

            var job = new Job(this.GetType().GetMethod("GetType"));
            var backgroundJob = new BackgroundJob(jobId, job, DateTime.MinValue);

            _context = new Lazy<ApplyStateContext>(
                () => new ApplyStateContext(
                    storage, connection, writeOnlyTransaction, backgroundJob,
                    NewStateValue,
                    OldStateValue));
        }

        public IState NewStateValue { get; set; }
        public string OldStateValue { get; set; }

        public ApplyStateContext Object => _context.Value;

        private RedisStorage CreateStorage()
        {
            var options = new RedisStorageOptions() { Db = RedisUtils.GetDb() };
            return new RedisStorage(RedisUtils.Connection, options);
        }

    }
}
