using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ScriptCacheCommandTests
{
    [Test]
    public async Task ImmediateCacheCommandsPreserveDigestOrderAndFlushModes()
    {
        await using var server = new FakeRespServer(
            "*3\r\n:1\r\n:0\r\n:1\r\n"u8.ToArray(),
            FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var flags = await client.WithKeyPrefix("tenant:").Scripts.ExistsAsync("first", "missing", "first");
        await client.Scripts.FlushAsync();
        await client.Scripts.FlushAsync(ScriptFlushMode.Sync);
        await client.Scripts.FlushAsync(ScriptFlushMode.Async);
        await Assert.That(flags).IsEquivalentTo([true, false, true]);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "SCRIPT EXISTS first missing first", "SCRIPT FLUSH", "SCRIPT FLUSH SYNC", "SCRIPT FLUSH ASYNC"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredCommandsUseReadOnlyEvalAndNodeLocalCache(bool transaction)
    {
        var script = RespireScript.Create("return KEYS[1]", readOnly: true);
        var shaReply = System.Text.Encoding.UTF8.GetBytes($"$40\r\n{script.Sha1}\r\n");
        byte[][] replies = [shaReply, "*1\r\n:1\r\n"u8.ToArray(), FakeRespServer.OkReply, "$10\r\ntenant:key\r\n"u8.ToArray()];
        if (transaction)
        {
            replies = [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
                "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), [.. "*4\r\n"u8, .. replies.SelectMany(x => x)]];
        }
        await using var server = new FakeRespServer(replies);
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        var scripts = transaction ? tx.Scripts : batch.Scripts;
        var load = scripts.Load(script);
        var exists = scripts.Exists(script.Sha1);
        var flush = scripts.Flush(ScriptFlushMode.Sync);
        var evaluation = scripts.Evaluate(script, ["key"]);
        if (transaction) await tx.CommitAsync(); else await batch.ExecuteAsync();
        await Assert.That(load.Result).IsEqualTo(script.Sha1);
        await Assert.That(exists.Result).IsEquivalentTo([true]);
        await Assert.That(flush.Result).IsTrue();
        using var result = evaluation.Result;
        await Assert.That(result.AsString()).IsEqualTo("tenant:key");
        var commands = server.ReceivedCommands.Where(x => x != "MULTI" && x != "EXEC").ToArray();
        await Assert.That(commands).IsEquivalentTo([
            "SCRIPT LOAD return KEYS[1]", $"SCRIPT EXISTS {script.Sha1}", "SCRIPT FLUSH SYNC",
            "EVAL_RO return KEYS[1] 1 tenant:key"]);
    }

    [Test]
    public async Task ReadOnlyCancellationDoesNotRetryOrAbandonFollowingReply()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), FakeRespServer.PongReply)
        {
            MinimumCommandsBeforeReply = 2,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return 1", readOnly: true),
            cancellationToken: cancellation.Token).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0) await Task.Delay(10, deadline.Token);
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        using var pong = await client.ExecuteAsync("PING");
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task InvalidInputsAreRejectedBeforeSending()
    {
        await using var client = RespireClient.Create(new RespireOptions { Protocol = RespProtocol.Resp2, Endpoints = { new RespireEndpoint("localhost") } });
        await Assert.That(async () => await client.Scripts.ExistsAsync()).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.Scripts.FlushAsync((ScriptFlushMode)99)).ThrowsExactly<ArgumentOutOfRangeException>();
        using var batch = client.CreateBatch();
        await Assert.That(() => batch.Scripts.Exists()).ThrowsExactly<ArgumentException>();
        await Assert.That(() => batch.Scripts.Flush((ScriptFlushMode)99)).ThrowsExactly<ArgumentOutOfRangeException>();
    }
}
