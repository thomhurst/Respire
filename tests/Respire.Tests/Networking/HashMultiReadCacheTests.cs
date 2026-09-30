using System.Runtime.InteropServices;
using System.Text;
using Respire.Protocol;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class HashMultiReadCacheTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MixedReadsShareFieldEntriesWithHGetAndPreserveOrder(bool raw)
    {
        await using var server = Server(command => command switch
        {
            "HGET hash a" => "$1\r\nA\r\n"u8.ToArray(),
            "HMGET hash b missing b" => "*3\r\n$1\r\nB\r\n$-1\r\n$1\r\nB\r\n"u8.ToArray(),
            _ => "-ERR unexpected read\r\n"u8.ToArray(),
        });
        await using var client = await ConnectAsync(server);
        await Assert.That(await client.Hashes.GetStringAsync("hash", "a")).IsEqualTo("A");
        string?[] values;
        if (raw)
        {
            using var response = await client.ExecuteAsync("HMGET", "hash", "a", "b", "missing", "b");
            values = response.Select(value => value.IsNull ? null : value.AsString()).ToArray();
            // Deliberately mutate the owned payload to prove cache and duplicate isolation.
            MemoryMarshal.GetReference(response[1].AsSpan()) = (byte)'X';
            await Assert.That(response[3].AsString()).IsEqualTo("B");
        }
        else values = await client.Hashes.GetManyAsync("hash", "a", "b", "missing", "b");
        await Assert.That(values).IsEquivalentTo(new string?[] { "A", "B", null, "B" });
        values[1] = "changed";
        await Assert.That(await client.Hashes.GetManyAsync("hash", "missing", "b", "a"))
            .IsEquivalentTo(new string?[] { null, "B", "A" });
        await Assert.That(await client.Hashes.GetStringAsync("hash", "b")).IsEqualTo("B");
        using var hit = await client.ExecuteAsync("HMGET", "hash", "b");
        MemoryMarshal.GetReference(hit[0].AsSpan()) = (byte)'Y';
        await Assert.That(await client.Hashes.GetStringAsync("hash", "b")).IsEqualTo("B");
        await Assert.That(server.ReceivedCommands.Where(IsHashRead))
            .IsEquivalentTo(["HGET hash a", "HMGET hash b missing b"]);
    }

    [Test]
    public async Task AllMissesSeedDifferentListsAndAbsentFields()
    {
        await using var server = Server(command => command switch
        {
            "HMGET hash a b" => "*2\r\n$1\r\nA\r\n$-1\r\n"u8.ToArray(),
            "HMGET hash c" => "*1\r\n$1\r\nC\r\n"u8.ToArray(),
            _ => "-ERR unexpected read\r\n"u8.ToArray(),
        });
        await using var client = await ConnectAsync(server);
        await Assert.That(await client.Hashes.GetManyAsync("hash", "a", "b"))
            .IsEquivalentTo(new string?[] { "A", null });
        await Assert.That(await client.Hashes.GetManyAsync("hash", "b", "c", "a"))
            .IsEquivalentTo(new string?[] { null, "C", "A" });
        await Assert.That(await client.Hashes.GetManyAsync("hash", "c", "b"))
            .IsEquivalentTo(new string?[] { "C", null });
        await Assert.That(server.ReceivedCommands.Where(IsHashRead))
            .IsEquivalentTo(["HMGET hash a b", "HMGET hash c"]);
    }

    [Test]
    [Arguments("+OK\r\n")]
    [Arguments("*-1\r\n")]
    [Arguments("*1\r\n$1\r\na\r\n")]
    [Arguments("*2\r\n$1\r\na\r\n:1\r\n")]
    [Arguments("*2\r\n$1\r\na\r\n-ERR nested\r\n")]
    public async Task MalformedRepliesNeverPublishPartialFields(string reply)
    {
        await using var server = Server(_ => Encoding.ASCII.GetBytes(reply));
        await using var client = await ConnectAsync(server);
        await Assert.That(async () => await client.Hashes.GetManyAsync("hash", "a", "b"))
            .ThrowsExactly<RespireProtocolException>();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InFlightInvalidationRejectsEveryField(bool clear)
    {
        await using var server = Server(_ => "*2\r\n$1\r\nA\r\n$1\r\nB\r\n"u8.ToArray());
        server.SuppressReply = command => command.StartsWith("HMGET ");
        await using var client = await ConnectAsync(server);
        var pending = client.Hashes.GetManyAsync("hash", "a", "b").AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Contains("HMGET hash a b"));
        if (clear) client.ClientSideCache!.Clear();
        else
        {
            await server.SendRawAsync(">2\r\n+invalidate\r\n*1\r\n$4\r\nhash\r\n"u8.ToArray());
            await WaitUntilAsync(() => client.ClientSideCache!.GetStatistics().Invalidations == 1);
        }
        await server.SendRawAsync("*2\r\n$1\r\nA\r\n$1\r\nB\r\n"u8.ToArray());
        await Assert.That(await pending.WaitAsync(Limit)).IsEquivalentTo(new string?[] { "A", "B" });
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        server.SuppressReply = null;
        await client.Hashes.GetManyAsync("hash", "a", "b");
        await Assert.That(server.ReceivedCommands.Count(command => command == "HMGET hash a b")).IsEqualTo(2);
    }

    [Test]
    public async Task LocalWriteInvalidatesAllFieldsOfTheHash()
    {
        var changed = false;
        await using var server = Server(command =>
        {
            if (command.StartsWith("HSET "))
            {
                changed = true;
                return ":1\r\n"u8.ToArray();
            }
            return Encoding.ASCII.GetBytes(changed
                ? "*2\r\n$1\r\nC\r\n$1\r\nB\r\n"
                : "*2\r\n$1\r\nA\r\n$1\r\nB\r\n");
        });
        await using var client = await ConnectAsync(server);
        await client.Hashes.GetManyAsync("hash", "a", "b");
        await client.Hashes.SetAsync("hash", "a", "C");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.Hashes.GetManyAsync("hash", "a", "b"))
            .IsEquivalentTo(new string?[] { "C", "B" });
        await Assert.That(server.ReceivedCommands.Count(command => command == "HMGET hash a b")).IsEqualTo(2);
    }

    [Test]
    public async Task BinaryFieldsSnapshotBytesAndShareWithTypedPrefixedReads()
    {
        await using var server = Server(command => command.StartsWith("HMGET ")
            ? "*2\r\n$1\r\nA\r\n$-1\r\n"u8.ToArray() : "-ERR unexpected read\r\n"u8.ToArray());
        server.SuppressReply = command => command.StartsWith("HMGET ");
        await using var client = await ConnectAsync(server);
        var view = client.WithKeyPrefix("tenant:");
        var field = new byte[] { 0, 255, 1 };
        var pending = view.ExecuteAsync("HMGET", "tenant:hash", field, Array.Empty<byte>()).AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Any(command => command.StartsWith("HMGET ")));
        field[2] = 2;
        await server.SendRawAsync("*2\r\n$1\r\nA\r\n$-1\r\n"u8.ToArray());
        using var first = await pending.WaitAsync(Limit);
        server.SuppressReply = null;
        using var hit = await client.ExecuteAsync("HMGET", "tenant:hash", Array.Empty<byte>(), new byte[] { 0, 255, 1 });
        await Assert.That(hit[0].IsNull).IsTrue();
        await Assert.That(hit[1].AsString()).IsEqualTo("A");
        // Raw commands use physical keys; typed facets apply the view's prefix.
        await Assert.That(await view.Hashes.GetStringAsync("hash", "")).IsNull();
        var reads = server.ReceivedArguments.Where(arguments => Encoding.ASCII.GetString(arguments[0]) == "HMGET").ToArray();
        await Assert.That(reads.Length).IsEqualTo(1);
        await Assert.That(reads[0][1]).IsEquivalentTo("tenant:hash"u8.ToArray());
        await Assert.That(reads[0][2]).IsEquivalentTo(new byte[] { 0, 255, 1 });
        await Assert.That(reads[0][3].Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterRedirectsPreserveTrackingAndOnlyMovedRepliesAreCached(bool moved)
    {
        await using var target = Server(command => command.StartsWith("HMGET ")
            ? "*2\r\n$1\r\nA\r\n$1\r\nB\r\n"u8.ToArray() : FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("hash");
        await using var seed = Server(command => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray()
            : Encoding.ASCII.GetBytes($"-{(moved ? "MOVED" : "ASK")} {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Endpoints = { new("127.0.0.1", seed.Port) },
            Connections = 1, Protocol = RespProtocol.Resp3, ClientSideCache = new(), CommandTimeout = Limit,
        });
        for (var read = 0; read < 2; read++)
            await Assert.That(await client.Hashes.GetManyAsync("hash", "a", "b"))
                .IsEquivalentTo(new string?[] { "A", "B" });
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(moved ? 2 : 0);
        await Assert.That(target.ReceivedCommands.Count(command => command == "HMGET hash a b")).IsEqualTo(moved ? 1 : 2);
        await Assert.That(target.ReceivedCommands.Count(command => command == "ASKING")).IsEqualTo(moved ? 0 : 2);
        await Assert.That(target.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(moved ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BroadcastCachesFieldsOnlyForCoveredHashes(bool covered)
    {
        await using var server = Server(_ => "*2\r\n$1\r\nA\r\n$-1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) }, Connections = 1,
            Protocol = RespProtocol.Resp3, CommandTimeout = Limit,
            ClientSideCache = new()
            {
                TrackingMode = RespireClientTrackingMode.Broadcast,
                BroadcastPrefixes = [covered ? "hash" : "other:"],
            },
        });
        for (var read = 0; read < 2; read++)
            await Assert.That(await client.Hashes.GetManyAsync("hash", "a", "b"))
                .IsEquivalentTo(new string?[] { "A", null });
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(covered ? 2 : 0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "HMGET hash a b"))
            .IsEqualTo(covered ? 1 : 2);
        await Assert.That(server.ReceivedCommands.Any(command => command == "CLIENT CACHING YES")).IsFalse();
    }

    [Test]
    public async Task ServerErrorsAndEmptyFieldListsStillReachRedis()
    {
        await using var server = Server(_ => "-ERR rejected\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        await Assert.That(async () => await client.Hashes.GetManyAsync("hash", "a"))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(async () => await client.Hashes.GetManyAsync("hash", Array.Empty<string>()))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Where(IsHashRead)).IsEquivalentTo(["HMGET hash a", "HMGET hash"]);
    }

    [Test]
    public async Task ExistingMGetStillRejectsCrossSlotListsEvenWhenEveryKeyIsCached()
    {
        await using var server = Server(command => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray()
            : "$1\r\nA\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Endpoints = { new("127.0.0.1", server.Port) }, Connections = 1,
            Protocol = RespProtocol.Resp3, ClientSideCache = new(), CommandTimeout = Limit,
        });
        await client.GetStringAsync("{a}");
        await client.GetStringAsync("{b}");
        await Assert.That(async () => await client.Strings.GetManyAsync("{a}", "{b}"))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("MGET "))).IsFalse();
    }

    private static bool IsHashRead(string command)
        => command.StartsWith("HGET ") || command.StartsWith("HMGET ");

    private static FakeRespServer Server(Func<string, byte[]> reply)
        => new(3, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello
                : command.StartsWith("CLIENT ") || command == "PING" ? FakeRespServer.OkReply : reply(command),
        };

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) }, Connections = 1,
            Protocol = RespProtocol.Resp3, ClientSideCache = new(), CommandTimeout = Limit, ConnectTimeout = Limit,
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
