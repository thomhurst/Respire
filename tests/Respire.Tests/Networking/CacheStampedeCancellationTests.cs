using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CacheStampedeCancellationTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AbandonedAcquisitionFencesBeforeTokenCheckedCleanup(bool timeout, bool successor)
    {
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? owner = null;
        var acquisitionConnection = -1;
        var fenced = false;
        var releasedBeforeFence = false;
        var releases = 0;
        await using var server = new FakeRespServer(6, FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return false;
            var args = server.ReceivedArguments[^1];
            if (args.Length != 9) return false;
            owner = Encoding.UTF8.GetString(args[6]);
            acquisitionConnection = server.ReceivedConnectionIds[^1];
            acquired.TrySetResult();
            return true; // SET succeeded but the response never arrives.
        };
        server.ReplyOverride = (connection, command) =>
        {
            if (command == "CLIENT ID") return Encoding.ASCII.GetBytes($":{connection + 1}\r\n");
            if (command.StartsWith("CLIENT KILL ID ", StringComparison.Ordinal))
            {
                if (acquisitionConnection >= 0 && command == $"CLIENT KILL ID {acquisitionConnection + 1}")
                {
                    server.CloseConnection(acquisitionConnection);
                    fenced = true;
                }
                return ":0\r\n"u8.ToArray();
            }
            if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return FakeRespServer.OkReply;
            var args = server.ReceivedArguments[^1];
            if (args.Length == 5) return "*4\r\n$-1\r\n:0\r\n:0\r\n$0\r\n\r\n"u8.ToArray();
            releasedBeforeFence |= !fenced;
            Interlocked.Increment(ref releases);
            if (owner == Encoding.UTF8.GetString(args[5])) owner = null;
            return ":1\r\n"u8.ToArray();
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = TimeSpan.FromSeconds(30), ThreadPoolMonitoring = false,
        });
        using var cancellation = new CancellationTokenSource();
        var factories = 0;
        var pending = client.GetOrSetAsync<string>("key", new RespireCacheAsideOptions(), _ =>
        {
            Interlocked.Increment(ref factories);
            return ValueTask.FromResult<string?>("unexpected");
        }, TimeSpan.FromMinutes(1), cancellation.Token).AsTask();
        await acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (successor) owner = "successor";
        if (timeout)
            await Assert.That(client.Core.Multiplexer.GetConnection().ExpireOldestCommandForTesting()).IsTrue();
        else
            cancellation.Cancel();
        if (timeout)
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).ThrowsExactly<RespireTimeoutException>();
        else
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
        await Assert.That(fenced).IsTrue();
        await Assert.That(releasedBeforeFence).IsFalse();
        await Assert.That(releases).IsEqualTo(1);
        await Assert.That(owner).IsEqualTo(successor ? "successor" : null);
        await Assert.That(factories).IsEqualTo(0);
    }
}
