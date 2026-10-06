using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

/// <summary>
/// Commands written from threads outside the pool dispatch the flush wake to the pool instead
/// of sending inline; they must still be sent and answered.
/// </summary>
public class FlushWakeTests
{
    [Test]
    public async Task LaterProducerFlushesWhenFirstProducerPausesAfterPublication()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Wait for the setup precondition; publication/release below controls the race itself.
        while (!connection.IsFlushLoopWaiting)
            await Task.Delay(1, deadline.Token);

        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Thread(() =>
        {
            try
            {
                using var reply = connection.SendAsync(new PausedProducer(published, release))
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                first.TrySetResult(reply.AsString());
            }
            catch (Exception error) { first.TrySetException(error); }
        }) { IsBackground = true };
        producer.Start();
        try
        {
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The second command did not make the buffer non-empty, but must still wake it.
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await Assert.That(first.Task.IsCompleted).IsFalse();
        }
        finally
        {
            release.Set();
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
            producer.Join(TimeSpan.FromSeconds(10));
        }
        await Assert.That(await first.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("PONG");
    }

    private readonly struct PausedProducer(TaskCompletionSource published, ManualResetEventSlim release) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) => writer.WriteRaw(FakeRespServer.PingFrame);
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken)
        {
            // This callback runs after the write gate is released and before ScheduleFlush.
            published.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Producer was not released.");
            return admissionToken;
        }
    }

    [Test]
    public async Task DedicatedThreadWriterOnAnIdleConnectionGetsItsReply()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var replies = new List<string>();
        Exception? failure = null;
        var writer = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    if (Thread.CurrentThread.IsThreadPoolThread)
                        throw new InvalidOperationException("The writer must not be a pool thread.");
                    // Each command starts a new batch on an idle connection, the dispatched path.
                    using var reply = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame))
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    replies.Add(reply.AsString()!);
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        writer.Start();

        await Assert.That(writer.Join(TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(replies.Count).IsEqualTo(20);
        await Assert.That(replies.All(reply => reply == "PONG")).IsTrue();
    }
}
