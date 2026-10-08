using System.Text;
using Respire.Testing;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Tests;

public class GeneratedHashModelIOTests
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task FakeRoundTrips(RespProtocol protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = protocol });
        await GeneratedHashModelScenarios.RoundTripAsync(client);
    }

    [Test]
    public async Task ExactCommandsPreserveTemplateUnicodePrefixAndSelectionOrder()
    {
        await using var server = new FakeRespServer(":3\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(),
            "*6\r\n$2\r\nId\r\n$6\r\né🙂\r\n$4\r\nName\r\n$0\r\n\r\n$5\r\nCount\r\n$1\r\n0\r\n"u8.ToArray(),
            "*3\r\n$-1\r\n$0\r\n\r\n$1\r\n0\r\n"u8.ToArray());
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = root.WithKeyPrefix("prefix:");
        var model = new StoredHashModel("é🙂", "", 0, null, null);
        await StoredHashModelHashMapper.SetAsync(client, model);
        await Assert.That(await StoredHashModelHashMapper.GetAsync(client, "model:{é🙂}")).IsEqualTo(model);
        var partial = await StoredHashModelHashMapper.GetPartialAsync(client, "model:{é🙂}", ["Age", "Name", "Count"]);
        await Assert.That(partial.Age.Selected && !partial.Age.Found && partial.Count.Found).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HSET prefix:model:{é🙂} Id é🙂 Name  Count 0",
            "HDEL prefix:model:{é🙂} Note Age",
            "HGETALL prefix:model:{é🙂}",
            "HMGET prefix:model:{é🙂} Age Name Count",
        });
    }

    [Test]
    public async Task ExactBinaryKeyAndPrefixBytes()
    {
        await using var server = new FakeRespServer(":3\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(), "*1\r\n$-1\r\n"u8.ToArray());
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = root.WithKeyPrefix(new byte[] { 0xfe, 0 });
        RespireKey key = new byte[] { 0xff, 0, 0x80 };
        await StoredHashModelHashMapper.SetAsync(client, key, new StoredHashModel("x", "", 0, null, null));
        await StoredHashModelHashMapper.GetAsync(client, key);
        await StoredHashModelHashMapper.GetPartialAsync(client, key, ["Count"]);
        foreach (var arguments in server.ReceivedArguments)
            await Assert.That(arguments[1]).IsEquivalentTo(new byte[] { 0xfe, 0, 0xff, 0, 0x80 });
    }

    [Test]
    public async Task BinaryKeyIsOwnedAcrossWriteCommands()
    {
        byte[] bytes = [0xff, 0, 0x80];
        await using var server = new FakeRespServer(":3\r\n"u8.ToArray(), ":0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) =>
            {
                if (command.StartsWith("HSET", StringComparison.Ordinal)) Array.Fill(bytes, (byte)0);
                return null;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await StoredHashModelHashMapper.SetAsync(client, bytes, new StoredHashModel("x", "", 0, null, null));
        await Assert.That(server.ReceivedArguments.Count).IsEqualTo(2);
        foreach (var arguments in server.ReceivedArguments)
            await Assert.That(arguments[1]).IsEquivalentTo(new byte[] { 0xff, 0, 0x80 });
    }

    [Test]
    public async Task RemovalErrorAfterSetPropagatesWithoutRollback()
    {
        await using var server = new FakeRespServer(":3\r\n"u8.ToArray(), "-ERR removal failed\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await StoredHashModelHashMapper.SetAsync(client, new StoredHashModel("x", "", 0, null, null)))
            .Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "HSET model:{x} Id x Name  Count 0", "HDEL model:{x} Note Age" });
    }

    [Test]
    [Arguments("*1\r\n$1\r\nf\r\n")]
    [Arguments("*2\r\n$1\r\nf\r\n$-1\r\n")]
    [Arguments("*2\r\n$1\r\nf\r\n:1\r\n")]
    [Arguments("*4\r\n$1\r\nf\r\n$1\r\na\r\n$1\r\nf\r\n$1\r\nb\r\n")]
    [Arguments("$-1\r\n")]
    [Arguments("+OK\r\n")]
    public async Task MalformedFullRepliesThrow(string reply)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await StoredHashModelHashMapper.GetAsync(client, "key")).Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments("*0\r\n")]
    [Arguments("*2\r\n$-1\r\n$-1\r\n")]
    [Arguments("*1\r\n:1\r\n")]
    [Arguments("%0\r\n")]
    [Arguments("$-1\r\n")]
    public async Task MalformedPartialRepliesThrow(string reply)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await StoredHashModelHashMapper.GetPartialAsync(client, "key", ["Count"])).Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments(":-1\r\n")]
    [Arguments(":99\r\n")]
    [Arguments("+OK\r\n")]
    public async Task MalformedWriteRepliesThrowBeforeRemoval(string reply)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await StoredHashModelHashMapper.SetAsync(client, new StoredHashModel("x", "", 0, null, null))).Throws<RespireProtocolException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidSelectionsAndRequiredNullFailBeforeIO()
    {
        await using var client = RespireClient.Create("localhost:1");
        foreach (string[] fields in new[] { Array.Empty<string>(), new[] { "unknown" }, new[] { "id" }, new[] { "Id", "Id" }, new[] { (string)null! } })
            await Assert.That(async () => await StoredHashModelHashMapper.GetPartialAsync(client, "key", fields)).Throws<ArgumentException>();
        await Assert.That(async () => await StoredHashModelHashMapper.SetAsync(client, new StoredHashModel("id", null!, 0, null, null))).Throws<ArgumentException>();
        await Assert.That(async () => await StoredHashModelHashMapper.GetPartialAsync(client, "key", null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task PreCanceledOperationsDoNotConnect()
    {
        await using var client = RespireClient.Create("localhost:1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await StoredHashModelHashMapper.SetAsync(client, new StoredHashModel("x", "", 0, null, null), cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await StoredHashModelHashMapper.GetAsync(client, "key", cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await StoredHashModelHashMapper.GetPartialAsync(client, "key", ["Count"], cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CancellationReachesInFlightPartialRead()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer("*1\r\n$-1\r\n"u8.ToArray())
        {
            SuppressReply = command => { if (!command.StartsWith("HMGET", StringComparison.Ordinal)) return false; received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var read = StoredHashModelHashMapper.GetPartialAsync(client, "key", ["Count"], cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await read).Throws<OperationCanceledException>();
    }
}
