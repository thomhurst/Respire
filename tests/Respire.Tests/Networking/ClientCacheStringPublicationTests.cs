using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCacheStringPublicationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FirstStringMissAndLocalHitReuseTheSameCanonicalString(bool coalesce, bool prefix)
    {
        await using var server = CreateServer("$7\r\né😀x\r\n"u8.ToArray());
        await using var root = await ConnectAsync(server, coalesce);
        var client = prefix ? root.WithKeyPrefix("tenant:") : root;
        var first = await client.GetStringAsync("key");
        var size = root.ClientSideCache!.GetStatistics().SizeBytes;
        await Assert.That(first).IsEqualTo("é😀x");
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(first);
        await Assert.That(root.ClientSideCache.GetStatistics().SizeBytes).IsEqualTo(size);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    public async Task CoalescedStringWaitersAndLocalHitReuseOneDecodedString()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("GET ", StringComparison.Ordinal)) return false;
            received.TrySetResult();
            return true;
        };
        await using var client = await ConnectAsync(server, coalesce: true);
        var first = client.GetStringAsync("key").AsTask();
        var second = client.GetStringAsync("key").AsTask();
        await received.Task.WaitAsync(Limit);
        await server.SendRawAsync("$7\r\né😀x\r\n"u8.ToArray());
        var text = await first.WaitAsync(Limit);
        await Assert.That(await second.WaitAsync(Limit)).IsSameReferenceAs(text);
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(text);
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DecodedStorageIsChargedBeforeTheFirstStringMissReturns(bool coalesce)
    {
        var text = new string('x', 100);
        var reply = Encoding.UTF8.GetBytes("$100\r\n" + text + "\r\n");
        await using var server = CreateServer(reply);
        await using var client = await ConnectAsync(server, coalesce, maxSizeBytes: 200);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo(text);
        await Assert.That(client.ClientSideCache!.GetStatistics().Count).IsEqualTo(0);
        await Assert.That(client.ClientSideCache.GetStatistics().SizeBytes).IsEqualTo(0);
    }

    private static FakeRespServer CreateServer(byte[] getReply)
        => new(Hello, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ") ? Hello
                : command.StartsWith("GET ") ? getReply : FakeRespServer.OkReply,
        };

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server, bool coalesce, long maxSizeBytes = 1_000_000)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            ClientSideCache = new() { CoalesceConcurrentMisses = coalesce, MaxSizeBytes = maxSizeBytes },
            CommandTimeout = Limit, ConnectTimeout = Limit,
        });
}
