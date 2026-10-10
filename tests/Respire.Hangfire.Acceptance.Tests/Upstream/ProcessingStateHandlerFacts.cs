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
    public class ProcessingStateHandlerFacts : UpstreamRedisTest
    {
        private const string JobId = "1";

        private readonly ApplyStateContextMock _context;
        private readonly Mock<IWriteOnlyTransaction> _transaction;

        public ProcessingStateHandlerFacts(RedisTestContainer fixture) : base(fixture)
        {
            _context = new ApplyStateContextMock(JobId);
            _transaction = new Mock<IWriteOnlyTransaction>();
        }

        [Test]
        public void StateName_ShouldBeEqualToProcessingState()
        {
            var handler = new ProcessingStateHandler();
            Assert.Equal(ProcessingState.StateName, handler.StateName);
        }

        [Test]
        public void Apply_ShouldAddTheJob_ToTheProcessingSet()
        {
            var handler = new ProcessingStateHandler();
            handler.Apply(_context.Object, _transaction.Object);

            _transaction.Verify(x => x.AddToSet(
                "processing", JobId, It.IsAny<double>()));
        }

        [Test]
        public void Unapply_ShouldRemoveTheJob_FromTheProcessingSet()
        {
            var handler = new ProcessingStateHandler();
            handler.Unapply(_context.Object, _transaction.Object);

            _transaction.Verify(x => x.RemoveFromSet("processing", JobId));
        }
    }
}
