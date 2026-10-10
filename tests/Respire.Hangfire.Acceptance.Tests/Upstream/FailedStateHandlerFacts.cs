#nullable disable
// Adapted from Hangfire.Redis.StackExchange 1.12.0, commit da8e39a33df204900afc30aeb65110f76f081c55.
// Changes: TUnit discovery, isolated Testcontainers fixture, and injected Respire shim.
// See NOTICE.md and License.md for upstream copyright and LGPLv3 terms.
using System;
using Hangfire.Redis.StackExchange;
using Hangfire.Redis.Tests.Utils;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Hangfire.Redis.Tests
{
    [ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
    [NotInParallel]
    public class FailedStateHandlerFacts : UpstreamRedisTest
    {
        private const string JobId = "1";

        private readonly ApplyStateContextMock _context;
        private readonly Mock<IWriteOnlyTransaction> _transaction;

        public FailedStateHandlerFacts(RedisTestContainer fixture) : base(fixture)
        {
            _context = new ApplyStateContextMock(JobId)
            {
                NewStateValue = new FailedState(new InvalidOperationException())
            };

            _transaction = new Mock<IWriteOnlyTransaction>();
        }

        [Test]
        public void StateName_ShouldBeEqualToFailedState()
        {
            var handler = new FailedStateHandler();
            Assert.Equal(FailedState.StateName, handler.StateName);
        }

        [Test]
        public void Apply_ShouldAddTheJob_ToTheFailedSet()
        {
            var handler = new FailedStateHandler();
            handler.Apply(_context.Object, _transaction.Object);

            _transaction.Verify(x => x.AddToSet(
                "failed", JobId, It.IsAny<double>()));
        }

        [Test]
        public void Unapply_ShouldRemoveTheJob_FromTheFailedSet()
        {
            var handler = new FailedStateHandler();
            handler.Unapply(_context.Object, _transaction.Object);

            _transaction.Verify(x => x.RemoveFromSet("failed", JobId));
        }
    }
}
