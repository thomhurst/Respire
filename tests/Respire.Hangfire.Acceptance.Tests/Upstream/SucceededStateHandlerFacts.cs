#nullable disable
// Adapted from Hangfire.Redis.StackExchange 1.12.0, commit da8e39a33df204900afc30aeb65110f76f081c55.
// Changes: TUnit discovery, isolated Testcontainers fixture, and injected Respire shim.
// See NOTICE.md and License.md for upstream copyright and LGPLv3 terms.
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
    public class SucceededStateHandlerFacts : UpstreamRedisTest
    {
        private const string JobId = "1";

        private readonly ApplyStateContextMock _context;
        private readonly Mock<IWriteOnlyTransaction> _transaction;

        public SucceededStateHandlerFacts(RedisTestContainer fixture) : base(fixture)
        {
            _context = new ApplyStateContextMock(JobId);
            _transaction = new Mock<IWriteOnlyTransaction>();
        }

        [Test]
        public void StateName_ShouldBeEqualToSucceededState()
        {
            var handler = new SucceededStateHandler();
            Assert.Equal(SucceededState.StateName, handler.StateName);
        }

        [Test]
        public void Apply_ShouldInsertTheJob_ToTheBeginningOfTheSucceededList_AndTrimIt()
        {
            var handler = new SucceededStateHandler();
            handler.Apply(_context.Object, _transaction.Object);

            _transaction.Verify(x => x.InsertToList(
                "succeeded", JobId));
            _transaction.Verify(x => x.TrimList(
                "succeeded", 0, 499));
        }

        [Test]
        public void Unapply_ShouldRemoveTheJob_FromTheSucceededList()
        {
            var handler = new SucceededStateHandler();
            handler.Unapply(_context.Object, _transaction.Object);

            _transaction.Verify(x => x.RemoveFromList("succeeded", JobId));
        }
    }
}
