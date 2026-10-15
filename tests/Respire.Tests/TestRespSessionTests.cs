using Respire.Protocol;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TestRespSessionTests
{
    [Test]
    [Arguments("factory")]
    [Arguments("HELLO")]
    [Arguments("SELECT")]
    public async Task CancelledSetupStopsPendingIoAndDisposesCreatedStream(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new PendingSetupStream(stage == "SELECT" ? "+OK\r\n"u8.ToArray() : []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RespireOptions
        {
            Endpoints = [new("localhost", 6379)],
            Protocol = RespireProtocol.Resp2,
            Database = stage == "SELECT" ? 1 : 0,
            TestingStreamFactory = async (_, _, token) =>
            {
                if (stage == "factory")
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return stream;
            },
        };
        var setup = TestRespSession.ConnectAsync(options, cancellation.Token);
        try
        {
            await (stage == "factory" ? entered.Task : stream.ReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.That(async () => await setup.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(stream.Disposed).IsEqualTo(stage != "factory");
        }
        finally
        {
            cancellation.Cancel();
            // Own any session returned if the cancellation assertion fails.
            try { await using var session = await setup; }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class PendingSetupStream(byte[] initialReply) : MemoryStream(initialReply)
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
