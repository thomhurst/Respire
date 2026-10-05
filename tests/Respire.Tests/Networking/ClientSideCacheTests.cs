using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using Respire.Internal;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientSideCacheTests
{
    private static readonly byte[] HelloReply = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    public async Task ClientCachingCommand_WritesExpectedFrame()
    {
        var buffer = new WriteBuffer(128);
        var writer = new RespWriter(buffer);
        new ClientCachingCommand().Write(ref writer);

        await Assert.That(buffer.WrittenMemory.ToArray())
            .IsEquivalentTo("*3\r\n$6\r\nCLIENT\r\n$7\r\nCACHING\r\n$3\r\nYES\r\n"u8.ToArray());
        buffer.Release();
    }

    [Test]
    public async Task ValidatedPrefix_WritesBothCommandsAndReturnsFinalReply()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port);
        var caching = new ClientCachingCommand();
        var get = new Cmd1(Verbs.Get, "key");

        var response = await connection.SendValidatedPrefixedAsync(
            in caching, in get, commandName: "GET");

        await Assert.That(response.AsString()).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "CLIENT CACHING YES",
            "GET key",
        ]);
        response.Dispose();
    }

    [Test]
    public async Task Get_MissIsTrackedAndSecondReadIsLocal()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        var first = await client.GetStringAsync("key");
        await Assert.That(first).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "HELLO 3",
            "CLIENT TRACKING ON OPTIN",
            "CLIENT CACHING YES",
            "GET key",
        ]);
        var statistics = client.ClientSideCache!.GetStatistics();
        await Assert.That(statistics.Hits).IsEqualTo(1);
        await Assert.That(statistics.Misses).IsEqualTo(1);
        await Assert.That(statistics.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExplicitZeroKeyMutation_FencesCacheWithoutReadingKeyArgument()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "+PONG\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, new RespireKey("unrelated"), "retained");

        using var pong = await client.ExecuteAsync(
            RespireCommand.Create("PING", RespireCacheMutation.Mutation));

        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).Contains("PING");
    }

    [Test]
    [Arguments(RespireCacheMutation.Unknown, 0)]
    [Arguments(RespireCacheMutation.ReadOnly, 1)]
    public async Task PrefixedRawDescriptor_PreservesExplicitCacheMutation(
        RespireCacheMutation mutation, int expectedCacheCount)
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server);
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, new RespireKey("unrelated"), "retained");
        var view = client.WithKeyPrefix("tenant:");

        using var result = await view.ExecuteAsync(
            RespireCommand.Create("JSON.SET", mutation), ["key", "$.field", "value"]);

        await Assert.That(cache.Count).IsEqualTo(expectedCacheCount);
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET tenant:key $.field value");
    }

    [Test]
    [Arguments(RespireCacheMutation.Unknown, 0)]
    [Arguments(RespireCacheMutation.ReadOnly, 1)]
    public async Task PrefixedRawDescriptorFireAndForget_PreservesExplicitCacheMutation(
        RespireCacheMutation mutation, int expectedCacheCount)
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server);
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, new RespireKey("unrelated"), "retained");
        var view = client.WithKeyPrefix("tenant:");

        await view.ExecuteFireAndForgetAsync(
            RespireCommand.Create("JSON.SET", mutation), ["key", "$.field", "value"]);

        await WaitUntilAsync(() => server.CommandsSeen >= 3);
        await Assert.That(cache.Count).IsEqualTo(expectedCacheCount);
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET tenant:key $.field value");
    }

    [Test]
    public async Task DeterministicRead_CachesIntegerReply()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            ":5\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.Strings.LengthAsync("key")).IsEqualTo(5);
        await Assert.That(await client.Strings.LengthAsync("key")).IsEqualTo(5);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "HELLO 3",
            "CLIENT TRACKING ON OPTIN",
            "CLIENT CACHING YES",
            "STRLEN key",
        ]);
        await Assert.That(client.ClientSideCache!.GetStatistics().Hits).IsEqualTo(1);
    }

    [Test]
    public async Task PreencodedParentReadUsesNormalizedSubcommandMutationMetadata()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            ":64\r\n"u8.ToArray(),
            "*0\r\n"u8.ToArray(),
            "*1\r\n:0\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server);
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, new RespireKey("unrelated"), "retained");

        using var memory = await client.ExecuteAsync(RespireCommands.Server.MEMORY, "USAGE", "key");
        using var config = await client.ExecuteAsync(RespireCommands.Server.CONFIG, "GET", "pattern");
        using var script = await client.ExecuteAsync(RespireCommands.Scripting.SCRIPT, "EXISTS", "sha1");

        await Assert.That(cache.Count).IsEqualTo(1);
        await client.ExecuteFireAndForgetAsync(RespireCommands.Server.MEMORY, "USAGE", "key");
        await client.ExecuteFireAndForgetAsync(RespireCommands.Server.CONFIG, "GET", "pattern");
        await client.ExecuteFireAndForgetAsync(RespireCommands.Scripting.SCRIPT, "EXISTS", "sha1");
        await WaitUntilAsync(() => server.CommandsSeen >= 8);

        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).Contains("MEMORY USAGE key");
        await Assert.That(server.ReceivedCommands).Contains("CONFIG GET pattern");
        await Assert.That(server.ReceivedCommands).Contains("SCRIPT EXISTS sha1");
    }

    [Test]
    public async Task AggregateRead_CachesDeepOwnedReply()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "*4\r\n$1\r\na\r\n$1\r\n1\r\n$1\r\nb\r\n$1\r\n2\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        var first = await client.Hashes.GetAllAsync("hash");
        var second = await client.Hashes.GetAllAsync("hash");

        await Assert.That(first).IsEquivalentTo(new Dictionary<string, string>
        {
            ["a"] = "1",
            ["b"] = "2",
        });
        await Assert.That(second).IsEquivalentTo(first);
        await Assert.That(server.ReceivedCommands.Count(static command => command == "HGETALL hash"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task CommandArguments_ArePartOfCacheIdentity()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$2\r\nab\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$2\r\nbc\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.Strings.GetRangeAsync("key", 0, 1)).IsEqualTo("ab");
        await Assert.That(await client.Strings.GetRangeAsync("key", 1, 2)).IsEqualTo("bc");
        await Assert.That(await client.Strings.GetRangeAsync("key", 0, 1)).IsEqualTo("ab");

        await Assert.That(server.ReceivedCommands.Count(static command => command.StartsWith("GETRANGE ")))
            .IsEqualTo(2);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task MultiKeyRead_IsEvictedWhenAnyDependencyIsInvalidated()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            ":3\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            ":4\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.Strings.LcsLengthAsync("first", "second")).IsEqualTo(3);
        await Assert.That(await client.Strings.LcsLengthAsync("first", "second")).IsEqualTo(3);

        await server.SendRawAsync(
            ">2\r\n+invalidate\r\n*1\r\n$6\r\nsecond\r\n"u8.ToArray());
        await WaitUntilAsync(() => client.ClientSideCache!.Count == 0);

        await Assert.That(await client.Strings.LcsLengthAsync("first", "second")).IsEqualTo(4);
        await Assert.That(server.ReceivedCommands.Count(static command => command == "LCS first second LEN"))
            .IsEqualTo(2);
    }

    [Test]
    public async Task CountedMultiKeyRead_TracksEveryDeclaredKey()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "*1\r\n$3\r\none\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "*1\r\n$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.SortedSets.IntersectAsync("first", "second"))
            .IsEquivalentTo(["one"]);
        await Assert.That(await client.SortedSets.IntersectAsync("first", "second"))
            .IsEquivalentTo(["one"]);

        await server.SendRawAsync(
            ">2\r\n+invalidate\r\n*1\r\n$6\r\nsecond\r\n"u8.ToArray());
        await WaitUntilAsync(() => client.ClientSideCache!.Count == 0);

        await Assert.That(await client.SortedSets.IntersectAsync("first", "second"))
            .IsEquivalentTo(["two"]);
        await Assert.That(server.ReceivedCommands.Count(static command => command == "ZINTER 2 first second"))
            .IsEqualTo(2);
    }

    [Test]
    public async Task InvalidationRacingGenericRead_PreventsStaleInsertion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            ":3\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            ":4\r\n"u8.ToArray());
        server.DelayReply(3, 250);
        await using var client = await ConnectAsync(server);

        var read = client.Strings.LengthAsync("key").AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 4);
        await server.SendRawAsync(">2\r\n+invalidate\r\n*1\r\n$3\r\nkey\r\n"u8.ToArray());

        await Assert.That(await read).IsEqualTo(3);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.Strings.LengthAsync("key")).IsEqualTo(4);
    }

    [Test]
    public async Task CursorAndRandomReads_AreNeverCached()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "*1\r\n$1\r\na\r\n"u8.ToArray(),
            "*1\r\n$1\r\nb\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.Sets.RandomMembersAsync("key", 1)).IsEquivalentTo(["a"]);
        await Assert.That(await client.Sets.RandomMembersAsync("key", 1)).IsEquivalentTo(["b"]);

        await Assert.That(server.ReceivedCommands.Count(static command => command.StartsWith("SRANDMEMBER ")))
            .IsEqualTo(2);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetLease_UsesTheCommandCacheWithoutSharingLeaseOwnership()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        using (var first = await client.Strings.GetLeaseAsync("key"))
        {
            await Assert.That(first.ToString()).IsEqualTo("value");
        }

        using (var second = await client.Strings.GetLeaseAsync("key"))
        {
            await Assert.That(second.ToString()).IsEqualTo("value");
        }

        await Assert.That(server.ReceivedCommands.Count(static command => command == "GET key"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task CatalogRead_UsesTheSameGenericCachePath()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            ":4\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        using (var first = await client.ExecuteAsync(RespireCommands.Hash.HSTRLEN, "hash", "field"))
        {
            await Assert.That(first.AsInteger()).IsEqualTo(4);
        }

        using (var second = await client.ExecuteAsync(RespireCommands.Hash.HSTRLEN, "hash", "field"))
        {
            await Assert.That(second.AsInteger()).IsEqualTo(4);
        }

        await Assert.That(server.ReceivedCommands.Count(static command => command == "HSTRLEN hash field"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task MultiwordRead_SharesCacheAcrossRawEntryPoints()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            ":42\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        var key = "key";

        using (var first = await client.ExecuteAsync($"MEMORY USAGE {key} SAMPLES {0}"))
        {
            await Assert.That(first.AsInteger()).IsEqualTo(42);
        }

        using (var second = await client.ExecuteAsync("MEMORY USAGE", key, "SAMPLES", 0))
        {
            await Assert.That(second.AsInteger()).IsEqualTo(42);
        }

        using (var third = await client.ExecuteAsync(
            RespireCommands.Server.MEMORY_USAGE, key, "SAMPLES", 0))
        {
            await Assert.That(third.AsInteger()).IsEqualTo(42);
        }

        await Assert.That(server.ReceivedCommands.Count(static command =>
            command == "MEMORY USAGE key SAMPLES 0"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task NormalizedReadOnlySubcommandPreservesCacheForBothExecutionPaths()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            ":42\r\n"u8.ToArray(),
            ":42\r\n"u8.ToArray(),
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        using (var result = await client.ExecuteAsync(RespireCommands.Server.MEMORY, "USAGE", "key", "SAMPLES", 0))
        {
            await Assert.That(result.AsInteger()).IsEqualTo(42);
        }
        await Assert.That(client.ClientSideCache!.Count).IsGreaterThanOrEqualTo(1);

        await client.ExecuteFireAndForgetAsync(RespireCommands.Server.MEMORY, "USAGE", "key", "SAMPLES", 0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(static command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task SampledMemoryUsage_BypassesClientCache()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            ":42\r\n"u8.ToArray(),
            ":43\r\n"u8.ToArray(),
            ":44\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.Server.MemoryUsageAsync("key")).IsEqualTo(41);
        await Assert.That(await client.Server.MemoryUsageAsync("key")).IsEqualTo(42);
        await Assert.That(await client.Server.MemoryUsageAsync("key", samples: 5)).IsEqualTo(43);
        await Assert.That(await client.Server.MemoryUsageAsync("key", samples: 5)).IsEqualTo(44);

        await Assert.That(server.ReceivedCommands.Count(static command => command == "MEMORY USAGE key"))
            .IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(static command =>
            command == "MEMORY USAGE key SAMPLES 5"))
            .IsEqualTo(2);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CacheDisruptingConnectionCommands_AreRejectedBeforeSend()
    {
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server);
        (RespireCommand Command, RespireValue[] Arguments)[] commands =
        [
            ("CLIENT TRACKING", ["OFF"]),
            ("CLIENT", ["TRACKING", "OFF"]),
            (RespireCommands.Connection.CLIENT_CACHING, ["YES"]),
            ("HELLO", [2]),
            ("RESET", []),
            ("SELECT", [1]),
        ];

        foreach (var (command, arguments) in commands)
        {
            await Assert.That(async () =>
                {
                    using var result = await client.ExecuteAsync(command, arguments);
                })
                .ThrowsExactly<NotSupportedException>();
        }

        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task GenericRead_SnapshotsCallerMemoryBeforeLazyConnect()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = CreateLazyClient(server);
        var field = new byte[] { (byte)'f' };

        var pending = client.ExecuteAsync(RespireCommands.Hash.HGET, ["hash", field]).AsTask();
        field[0] = (byte)'x';
        using (var first = await pending)
        {
            await Assert.That(first.AsString()).IsEqualTo("value");
        }

        using (var second = await client.ExecuteAsync(
            RespireCommands.Hash.HGET, ["hash", new byte[] { (byte)'f' }]))
        {
            await Assert.That(second.AsString()).IsEqualTo("value");
        }

        await Assert.That(server.ReceivedCommands.Count(static command => command == "HGET hash f"))
            .IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).DoesNotContain("HGET hash x");
    }

    [Test]
    public async Task Get_SnapshotsCallerKeyBeforeLazyConnect()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = CreateLazyClient(server);
        var key = new byte[] { (byte)'a' };

        var pending = client.GetStringAsync(key).AsTask();
        key[0] = (byte)'b';

        await Assert.That(await pending).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync(new byte[] { (byte)'a' })).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(static command => command == "GET a"))
            .IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).DoesNotContain("GET b");
    }

    [Test]
    public async Task MGet_SnapshotsCallerKeysBeforeLazyConnect()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray());
        await using var client = CreateLazyClient(server);
        var first = new byte[] { (byte)'a' };
        var second = new byte[] { (byte)'b' };

        var pending = client.Strings.GetManyAsync(first, second).AsTask();
        first[0] = (byte)'x';
        second[0] = (byte)'y';

        await Assert.That(await pending).IsEquivalentTo((string?[])["a", "b"]);
        await Assert.That(await client.Strings.GetManyAsync(
            new byte[] { (byte)'a' }, new byte[] { (byte)'b' }))
            .IsEquivalentTo((string?[])["a", "b"]);
        await Assert.That(server.ReceivedCommands.Count(static command => command == "MGET a b"))
            .IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).DoesNotContain("MGET x y");
    }

    [Test]
    public async Task StructuredReadCommand_UsesSemanticCacheIdentity()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "*1\r\n:7\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        var operation = BitFieldOperation.Get(BitFieldEncoding.Unsigned(8), BitFieldOffset.Bits(0));

        await Assert.That(await client.Bitmaps.FieldReadOnlyAsync("key", operation))
            .IsEquivalentTo((long?[])[7]);
        await Assert.That(await client.Bitmaps.FieldReadOnlyAsync("key", operation))
            .IsEquivalentTo((long?[])[7]);

        await Assert.That(server.ReceivedCommands.Count(static command => command == "BITFIELD_RO key GET u8 0"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task BitFieldReadOnly_CacheDistinguishesSignednessWidthAndOffset()
    {
        await using var server = new FakeRespServer(
            HelloReply, FakeRespServer.OkReply,
            FakeRespServer.OkReply, "*1\r\n:255\r\n"u8.ToArray(),
            FakeRespServer.OkReply, "*1\r\n:-1\r\n"u8.ToArray(),
            FakeRespServer.OkReply, "*1\r\n:127\r\n"u8.ToArray(),
            FakeRespServer.OkReply, "*1\r\n:42\r\n"u8.ToArray(),
            FakeRespServer.OkReply, "*1\r\n:63\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        BitFieldOperation[] operations =
        [
            BitFieldOperation.Get(BitFieldEncoding.Unsigned(8), BitFieldOffset.Bits(1)),
            BitFieldOperation.Get(BitFieldEncoding.Signed(8), BitFieldOffset.Bits(1)),
            BitFieldOperation.Get(BitFieldEncoding.Unsigned(7), BitFieldOffset.Bits(1)),
            BitFieldOperation.Get(BitFieldEncoding.Unsigned(8), BitFieldOffset.Fields(1)),
            BitFieldOperation.Get(BitFieldEncoding.Unsigned(8), BitFieldOffset.Bits(2)),
        ];
        long[] expected = [255, -1, 127, 42, 63];

        for (var pass = 0; pass < 2; pass++)
        {
            for (var index = 0; index < operations.Length; index++)
            {
                var result = await client.Bitmaps.FieldReadOnlyAsync("key", operations[index]);
                await Assert.That(result[0]).IsEqualTo(expected[index]);
            }
        }

        await Assert.That(server.ReceivedCommands.Count(static command => command.StartsWith("BITFIELD_RO ")))
            .IsEqualTo(operations.Length);
    }

    [Test]
    public async Task GeoSearchAny_BypassesClientCache()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "*1\r\n$3\r\none\r\n"u8.ToArray(),
            "*1\r\n$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        var options = new GeoSearchOptions { Count = 1, Any = true };

        await Assert.That((await client.Geo.SearchAsync(
            "places",
            GeoSearchOrigin.FromMember("origin"),
            GeoSearchShape.Circle(1),
            options))[0].Member).IsEqualTo("one");
        await Assert.That((await client.Geo.SearchAsync(
            "places",
            GeoSearchOrigin.FromMember("origin"),
            GeoSearchShape.Circle(1),
            options))[0].Member).IsEqualTo("two");

        await Assert.That(server.ReceivedCommands.Count(static command =>
            command == "GEOSEARCH places FROMMEMBER origin BYRADIUS 1 m COUNT 1 ANY"))
            .IsEqualTo(2);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ClientCachingError_IsSurfacedAndReadIsNotCached()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "-ERR caching disabled\r\n"u8.ToArray(),
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        var error = await Assert.That(async () => await client.GetStringAsync("key"))
            .ThrowsExactly<RespireServerException>();

        await Assert.That(error!.CommandName).IsEqualTo("GET");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.TakeLast(2)).IsEquivalentTo([
            "CLIENT CACHING YES", "GET key",
        ]);
    }

    [Test]
    public async Task InvalidationPush_EvictsKeyAndForcesTrackedRead()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\none\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("one");
        await server.SendRawAsync(">2\r\n+invalidate\r\n*1\r\n$3\r\nkey\r\n"u8.ToArray());
        await WaitUntilAsync(() => client.ClientSideCache!.Count == 0);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("two");
        await Assert.That(server.ReceivedCommands.Count(static command => command == "GET key"))
            .IsEqualTo(2);
    }

    [Test]
    public async Task NullInvalidation_FlushesAllEntries()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$1\r\na\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$1\r\nb\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        await server.SendRawAsync(">2\r\n+invalidate\r\n_\r\n"u8.ToArray());
        await WaitUntilAsync(() => client.ClientSideCache!.Count == 0);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("b");
    }

    [Test]
    public async Task InvalidationRacingReadResponse_PreventsStaleInsertion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        server.DelayReply(3, 250);
        await using var client = await ConnectAsync(server);

        var read = client.GetStringAsync("key");
        await WaitUntilAsync(() => server.CommandsSeen >= 4);
        await server.SendRawAsync(">2\r\n+invalidate\r\n*1\r\n$3\r\nkey\r\n"u8.ToArray());

        await Assert.That(await read).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task LocalMutation_EagerlyInvalidatesBeforeReply()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        await client.SetAsync("key", "new");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task MutationCompletion_RejectsReadStartedAfterEagerInvalidation()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        server.MinimumCommandsBeforeReply = 3;
        var mutation = client.SetAsync("key", "new").AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var racingRead = client.GetStringAsync("key").AsTask();

        await mutation;
        await Assert.That(await racingRead).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        server.MinimumCommandsBeforeReply = 1;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task StringFastPathMutation_UsesCompletionFence()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray());
        server.DelayReply(2, 250);
        await using var client = await ConnectAsync(server);

        var mutation = client.Strings.GetAndDeleteAsync("key").AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 3);
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, "key", "old");

        await Assert.That(await mutation).IsEqualTo("old");
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BroadMutationCompletion_RejectsReadStartedAfterFlush()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        server.MinimumCommandsBeforeReply = 3;
        var mutation = client.Strings.SetManyAsync(("key", "new"), ("other", "value")).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var racingRead = client.GetStringAsync("key").AsTask();

        await mutation;
        await Assert.That(await racingRead).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        server.MinimumCommandsBeforeReply = 1;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task PfCount_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var count = client.HyperLogLog.CountAsync("key").AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        await Assert.That(await count).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BatchCompletion_RejectsReadStartedDuringExecution()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        using var batch = client.CreateBatch();
        _ = batch.Strings.Set("key", "new");
        server.MinimumCommandsBeforeReply = 3;
        var execution = batch.ExecuteAsync().AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var racingRead = client.GetStringAsync("key").AsTask();

        await execution;
        await Assert.That(await racingRead).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        server.MinimumCommandsBeforeReply = 1;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task TransactionCompletion_FlushesEntriesInsertedDuringExecution()
    {
        await using var server = new FakeRespServer(
            2,
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "*1\r\n+OK\r\n"u8.ToArray());
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);
        await using var transaction = client.CreateTransaction();
        _ = transaction.Strings.Set("key", "new");

        var commit = transaction.CommitAsync().AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, "key", "old");
        await Assert.That(cache.Count).IsEqualTo(1);

        await commit;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NativeScript_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var script = RespireScript.Create("return redis.call('DEL', KEYS[1])");
        var execution = client.Scripts.ExecuteAsync(script, ["key"]).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        using var result = await execution;

        await Assert.That(result.AsInteger()).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task RawStoredProcedure_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var execution = client.ExecuteAsync(
            RespireCommands.Scripting.EVAL, ["return redis.call('DEL', KEYS[1])", 1, "key"]).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        using var result = await execution;

        await Assert.That(result.AsInteger()).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BlockingMutation_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            2,
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray());
        server.SuppressReply = command => command.StartsWith("BLPOP ", StringComparison.Ordinal);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var execution = client.ExecuteAsync(RespireCommands.List.BLPOP, ["key", 0]).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 6);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(execution.IsCompleted).IsFalse();
        InsertCachedValue(cache, "key", "old");
        await server.SendRawAsync(FakeRespServer.OkReply, server.ReceivedConnectionIds.Last());

        using var result = await execution;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CorrectionScript_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => HelloReply,
                "CLIENT ID" => ":123\r\n"u8.ToArray(),
                _ when command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) => ":0\r\n"u8.ToArray(),
                "GET key" => "$3\r\nold\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        server.SuppressReply = command => command.StartsWith("EVAL", StringComparison.Ordinal);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var script = RespireScript.Create("return redis.call('DEL', KEYS[1])");
        var execution = client.ExecuteOnAllConnectionsAsync(script, ["key"], []).AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal)));
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        await server.SendRawAsync(":1\r\n"u8.ToArray());
        await execution;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TrackedScriptResponse_OwnsCompletionFence()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => HelloReply,
                "CLIENT ID" => ":123\r\n"u8.ToArray(),
                _ when command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) => ":0\r\n"u8.ToArray(),
                "GET key" => "$3\r\nold\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        server.SuppressReply = command => command.StartsWith("EVAL", StringComparison.Ordinal);
        await using var client = await ConnectAsync(server);
        await client.EnsureReliableCorrectionOrderingAsync();

        await client.GetStringAsync("key");
        var script = RespireScript.Create("return redis.call('DEL', KEYS[1])");
        var execution = await client.StartTrackedScriptExecutionAsync(
            script,
            ["key"],
            [],
            CancellationToken.None);
        await WaitUntilAsync(() => server.ReceivedCommands.Any(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)));
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        await server.SendRawAsync(":1\r\n"u8.ToArray());
        using var result = await execution.Response;

        await Assert.That(result.AsInteger()).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MGet_CachesPerKeyAndFetchesOnlyInvalidatedMisses()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "*1\r\n$2\r\nb2\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.Strings.GetManyAsync("a", "b"))
            .IsEquivalentTo((string?[])["a", "b"]);
        await Assert.That(await client.Strings.GetManyAsync("a", "b"))
            .IsEquivalentTo((string?[])["a", "b"]);
        await client.SetAsync("b", "b2");
        await Assert.That(await client.Strings.GetManyAsync("a", "b"))
            .IsEquivalentTo((string?[])["a", "b2"]);

        await Assert.That(server.ReceivedCommands[^1]).IsEqualTo("MGET b");
    }

    [Test]
    public async Task PrefixedViews_UseResolvedWireKeyIdentity()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\none\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        var first = client.WithKeyPrefix("a:");
        var second = client.WithKeyPrefix("b:");

        await Assert.That(await first.GetStringAsync("key")).IsEqualTo("one");
        await Assert.That(await second.GetStringAsync("key")).IsEqualTo("two");
        await Assert.That(await first.GetStringAsync("key")).IsEqualTo("one");

        await Assert.That(server.ReceivedCommands).Contains("GET a:key");
        await Assert.That(server.ReceivedCommands).Contains("GET b:key");
    }

    [Test]
    public async Task ClusterAskRedirect_TracksReadOnTarget()
    {
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray())
        {
            MinimumCommandsBeforeReply = 2,
        };
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "*0\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);

        await Assert.That(target.ReceivedCommands.Take(2)).IsEquivalentTo([
            "HELLO 3", "CLIENT TRACKING ON OPTIN",
        ]);
        await Assert.That(target.ReceivedCommands.Skip(2)).IsEquivalentTo([
            "ASKING", "CLIENT CACHING YES", "GET key",
        ]);
    }

    [Test]
    public async Task OrdinaryClusterRedirect_FlushesCacheContinuity()
    {
        var slot = ClusterHash.GetSlot("key");
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            ":3\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "*0\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);

        await Assert.That(await client.Strings.LengthAsync("key")).IsEqualTo(3);

        await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes).IsEqualTo(1);
    }

    [Test]
    public async Task ClusterFireAndForget_FencesCacheThroughCompletion()
    {
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "*0\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        seed.DelayReply(5, 250);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await client.GetStringAsync("key");
        var write = client.ExecuteFireAndForgetAsync(
            RespireCommands.String.SET, "key", "new").AsTask();
        await WaitUntilAsync(() => seed.CommandsSeen >= 6);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        await write;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StandaloneFireAndForget_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var write = client.ExecuteFireAndForgetAsync(
            RespireCommands.String.SET, "key", "new").AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        await write;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ConnectionFailure_FlushesCacheBeforePendingCallerResumes()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply)
        {
            CloseConnectionAfterCommand = 6,
        };
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        var pending = client.Strings.LengthAsync("other").AsTask();

        await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes).IsEqualTo(1);
    }

    [Test]
    [Arguments(ServerFlushMode.Default, "")]
    [Arguments(ServerFlushMode.Sync, " SYNC")]
    [Arguments(ServerFlushMode.Async, " ASYNC")]
    public async Task ClusterFlush_FencesCacheThroughCompletion(ServerFlushMode mode, string suffix)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "FLUSHDB" + suffix) return false;
                arrived.TrySetResult();
                return true;
            },
        };
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            AllowAdmin = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await client.GetStringAsync("key");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);

        var flush = client.Server.FlushDatabaseAsync(mode).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        InsertCachedValue(client.Core.ClientCache!, "key", "value");

        await target.SendRawAsync(FakeRespServer.OkReply);
        await flush;

        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(target.ReceivedCommands[^1]).IsEqualTo("FLUSHDB" + suffix);
    }

    [Test]
    public async Task RawClusterWideCommand_FencesCacheThroughCompletion()
    {
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        target.DelayReply(4, 250);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await client.GetStringAsync("key");
        var flush = client.ExecuteAsync(RespireCommands.Scripting.SCRIPT_FLUSH).AsTask();
        await WaitUntilAsync(() => target.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "value");

        using var result = await flush;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RawClusterWideFireAndForget_ClearsCache()
    {
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await client.GetStringAsync("key");
        await client.ExecuteFireAndForgetAsync(RespireCommands.Scripting.SCRIPT_FLUSH);
        await WaitUntilAsync(() => target.CommandsSeen >= 5);

        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(target.ReceivedCommands[^1]).IsEqualTo("SCRIPT FLUSH");
    }

    [Test]
    public async Task ReadOnlyScriptPreservesCachedValues()
    {
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply,
            FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray(),
            "-NOSCRIPT missing\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        await client.GetStringAsync("key");
        var script = RespireScript.Create("return 1", readOnly: true);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script)).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.GetStatistics().Hits).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(6);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScriptExistsRespectsHostCachePolicy(bool deferred)
    {
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply,
            FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray(), "*1\r\n:0\r\n"u8.ToArray(),
            FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        await client.GetStringAsync("key");
        if (deferred)
        {
            using var batch = client.CreateBatch();
            var pending = batch.Scripts.Exists("digest");
            await batch.ExecuteAsync();
            await Assert.That(pending.Result).IsEquivalentTo([false]);
        }
        else
        {
            await Assert.That(await client.Scripts.ExistsAsync("digest")).IsEquivalentTo([false]);
        }
        // Batches retain their existing conservative whole-batch invalidation policy.
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(deferred ? 0 : 1);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(deferred ? 0 : 1);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(deferred ? 7 : 5);
    }

    [Test]
    [Arguments(true, ScriptFlushMode.Default)]
    [Arguments(false, ScriptFlushMode.Default)]
    [Arguments(false, ScriptFlushMode.Sync)]
    [Arguments(false, ScriptFlushMode.Async)]
    public async Task ScriptCacheManagementPreservesCachedValues(bool load, ScriptFlushMode mode)
    {
        var script = RespireScript.Create("return 1");
        var reply = load ? Encoding.ASCII.GetBytes($"$40\r\n{script.Sha1}\r\n") : FakeRespServer.OkReply;
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply,
            FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray(), reply);
        await using var client = await ConnectAsync(server);
        await client.GetStringAsync("key");
        if (load)
        {
            await Assert.That(await client.Scripts.LoadAsync(script)).IsEqualTo(script.Sha1);
        }
        else
        {
            await client.Scripts.FlushAsync(mode);
        }
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(5);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SortReadOnlyPreservesUnrelatedCachedValues(bool readOnly)
    {
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply,
            FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(),
            FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        await client.GetStringAsync("key");
        // GET # prevents result caching, while SORT_RO must retain its read-only classification.
        await client.Keys.SortAsync("items", new RespireSortOptions { ReadOnly = readOnly, Get = new RespireKey[] { "#" } });
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(readOnly ? 1 : 0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(readOnly ? 1 : 0);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(readOnly ? 5 : 7);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MSetNx_FencesAllKeysBeforeAndAfterCompletion(bool applied)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("MSETNX ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await ConnectAsync(server);
        var view = client.WithKeyPrefix("tenant:");
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, "tenant:a", "old");
        InsertCachedValue(cache, "tenant:b", "old");
        var write = view.Strings.SetManyIfNotExistsAsync(("a", "new"), ("b", "new")).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "tenant:a", "racing");
        InsertCachedValue(cache, "tenant:b", "racing");
        await server.SendRawAsync(Encoding.ASCII.GetBytes(applied ? ":1\r\n" : ":0\r\n"));
        await Assert.That(await write).IsEqualTo(applied);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("SET", false)]
    [Arguments("SET", true)]
    [Arguments("DELEX", false)]
    [Arguments("DELEX", true)]
    [Arguments("DELIFEQ", false)]
    [Arguments("DELIFEQ", true)]
    public async Task ComparisonMutation_FencesOnlyTargetBeforeAndAfterReply(string command, bool applied)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply)
        {
            SuppressReply = value =>
            {
                if (!value.StartsWith(command + " ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await ConnectAsync(server);
        var view = client.WithKeyPrefix("tenant:");
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, "tenant:key", "old");
        InsertCachedValue(cache, "tenant:other", "untouched");
        var condition = RespireValueCondition.EqualTo("old");
        var pending = command switch
        {
            "SET" => view.Strings.SetConditionalAsync("key", (RespireValue)"new", condition).AsTask(),
            "DELEX" => view.Strings.DeleteConditionalAsync("key", condition).AsTask(),
            _ => view.Strings.DeleteIfEqualAsync("key", "old").AsTask(),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(1);
        InsertCachedValue(cache, "tenant:key", "racing");
        byte[] reply = command == "SET"
            ? (applied ? FakeRespServer.OkReply : "$-1\r\n"u8.ToArray())
            : (applied ? ":1\r\n"u8.ToArray() : ":0\r\n"u8.ToArray());
        await server.SendRawAsync(reply);
        await Assert.That(await pending).IsEqualTo(applied);
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(await view.GetStringAsync("other")).IsEqualTo("untouched");
    }

    [Test]
    public async Task Digest_PreservesCachedReads()
    {
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply, FakeRespServer.OkReply, "$16\r\n0123456789abcdef\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        InsertCachedValue(client.Core.ClientCache!, "tenant:key", "old");
        var view = client.WithKeyPrefix("tenant:");
        await Assert.That(await view.Strings.DigestAsync("key")).IsEqualTo("0123456789abcdef");
        await Assert.That(await view.Strings.DigestAsync("key")).IsEqualTo("0123456789abcdef");
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["HELLO 3", "CLIENT TRACKING ON OPTIN", "CLIENT CACHING YES", "DIGEST tenant:key"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamMetadataMutationFencesOnlyItsTarget(bool setLastId)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith(setLastId ? "XSETID " : "XGROUP CREATECONSUMER ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await ConnectAsync(server);
        var view = client.WithKeyPrefix("tenant:");
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, "tenant:key", "old");
        InsertCachedValue(cache, "tenant:other", "untouched");
        Task pending = setLastId
            ? view.Streams.SetLastIdAsync("key", "1-0", entriesAdded: 1, maxDeletedId: "0-0").AsTask()
            : view.Streams.CreateConsumerAsync("key", "workers", "consumer").AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(1);
        InsertCachedValue(cache, "tenant:key", "racing");
        await server.SendRawAsync(setLastId ? FakeRespServer.OkReply : ":1\r\n"u8.ToArray());
        await pending;
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(await view.GetStringAsync("other")).IsEqualTo("untouched");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task NativeLockMutationKeepsCacheFencesAcrossTheReply(bool extend, bool applied)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.Contains(" IFEQ ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await ConnectAsync(server);
        var view = client.WithKeyPrefix("tenant:");
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, "tenant:key", "owner");
        var pending = extend
            ? view.Locks.ResetExpiryAsync("key", "owner", TimeSpan.FromSeconds(30)).AsTask()
            : view.Locks.ReleaseAsync("key", "owner").AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "tenant:key", "racing-read");
        byte[] reply = extend
            ? (applied ? FakeRespServer.OkReply : "$-1\r\n"u8.ToArray())
            : (applied ? ":1\r\n"u8.ToArray() : ":0\r\n"u8.ToArray());
        await server.SendRawAsync(reply);
        await Assert.That(await pending).IsEqualTo(applied);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0, ServerFlushMode.Default)]
    [Arguments(0, ServerFlushMode.Sync)]
    [Arguments(0, ServerFlushMode.Async)]
    [Arguments(1, ServerFlushMode.Default)]
    [Arguments(1, ServerFlushMode.Sync)]
    [Arguments(1, ServerFlushMode.Async)]
    [Arguments(2, ServerFlushMode.Default)]
    [Arguments(2, ServerFlushMode.Sync)]
    [Arguments(2, ServerFlushMode.Async)]
    public async Task ServerFlushModesInvalidateCachedValues(int execution, ServerFlushMode mode)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                var hello when hello.StartsWith("HELLO", StringComparison.Ordinal) => HelloReply,
                "EXEC" => "*1\r\n+OK\r\n"u8.ToArray(),
                var flush when execution == 2 && flush.StartsWith("FLUSH", StringComparison.Ordinal)
                    => "+QUEUED\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, AllowAdmin = true, Connections = 1,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            ClientSideCache = new(),
        });
        InsertCachedValue(client.Core.ClientCache!, "key", "old");
        if (execution == 0)
            await client.Server.FlushAllAsync(mode);
        else
        {
            using var batch = client.CreateBatch();
            await using var tx = client.CreateTransaction();
            IRespireCommandQueue queue = execution == 2 ? tx : batch;
            var flush = queue.Server.FlushAll(mode);
            await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
            if (execution == 2) await tx.CommitAsync(); else await batch.ExecuteAsync();
            await Assert.That(flush.Result).IsTrue();
        }
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            ClientSideCache = new(),
        });

    private static RespireClient CreateLazyClient(FakeRespServer server)
        => RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            ClientSideCache = new(),
        });

    private static void InsertCachedValue(
        ClientSideCacheCoordinator cache,
        RespireKey key,
        string value)
    {
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString(Encoding.UTF8.GetBytes(value));
        cache.CompleteRead(in token, in response, allowInsert: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
