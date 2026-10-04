using Respire.Commands;
using Respire.Networking;
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
