using Respire.Protocol;
using Respire.Commands;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientSideCacheCoordinatorTests
{
    [Test]
    [Arguments("MSET")]
    [Arguments("MSETNX")]
    [Arguments("DEL")]
    [Arguments("UNLINK")]
    public async Task KnownMultiKeyMutationsFenceOnlyTheirKeys(string operation)
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "first", "old");
        Insert(cache, "second", "old");
        Insert(cache, "unrelated", "retained");
        var arguments = operation switch
        {
            "MSET" or "MSETNX" => new RespireValue[] { "first", "new", "second", "new" },
            _ => ["first", "second"],
        };
        var verb = operation switch
        {
            "MSET" => Verbs.MSet,
            "MSETNX" => RespireCommands.String.MSETNX.Verb,
            "DEL" => Verbs.Del,
            "UNLINK" => Verbs.Unlink,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var command = new CmdN(verb, arguments);

        var fence = cache.BeforeCommand(operation, in command);

        await Assert.That(fence.IsRequired).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
        await Assert.That(cache.TryGet(new RespireKey("first"), out _)).IsFalse();
        await Assert.That(cache.TryGet(new RespireKey("second"), out _)).IsFalse();

        var firstKey = new RespireKey("first");
        var secondKey = new RespireKey("second");
        var firstRead = cache.BeginRead(in firstKey);
        var secondRead = cache.BeginRead(in secondKey);
        cache.CompleteMutation(in fence);
        var staleFirst = RespValue.BulkString("stale-first"u8.ToArray());
        var staleSecond = RespValue.BulkString("stale-second"u8.ToArray());
        cache.CompleteRead(in firstRead, in staleFirst, allowInsert: true);
        cache.CompleteRead(in secondRead, in staleSecond, allowInsert: true);

        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
        await Assert.That(cache.TryGet(in firstKey, out _)).IsFalse();
        await Assert.That(cache.TryGet(in secondKey, out _)).IsFalse();
    }

    [Test]
    public async Task OneKeyMultiKeyMutationKeepsFenceInline()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "only", "old");
        Insert(cache, "unrelated", "retained");
        var command = new CmdN(Verbs.MSet, ["only", "new"]);

        var fence = cache.BeforeCommand("MSET", in command);

        await Assert.That(fence.IsRequired).IsTrue();
        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.Key);
        await Assert.That(fence.Keys).IsNull();
        await Assert.That(fence.Key).IsEqualTo(new RespireKey("only"));
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
    }

    [Test]
    public async Task JsonMSetFencesTripletKeysAndLeavesUnrelatedEntries()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "json:first", "old");
        Insert(cache, "json:second", "old");
        Insert(cache, "unrelated", "retained");
        var command = new CatalogCommand(RespireCommands.Json.JSON_MSET,
            ["json:first", "$", "new", "json:second", "$", "new"]);

        var fence = cache.BeforeCommand("JSON.MSET", in command);

        await Assert.That(fence.IsRequired).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(cache.TryGet(new RespireKey("json:first"), out _)).IsFalse();
        await Assert.That(cache.TryGet(new RespireKey("json:second"), out _)).IsFalse();
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");

        cache.CompleteMutation(in fence);
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
    }

    [Test]
    public async Task TypedMSetExFencesItsCountedPairKeys()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "first", "old");
        Insert(cache, "second", "old");
        Insert(cache, "unrelated", "retained");
        var command = new MSetExCommand(RespireCommands.String.MSETEX.Verb,
            [2, "first", "new", "second", "new", "PX", 1000]);

        var fence = cache.BeforeCommand("MSETEX", in command);

        await Assert.That(fence.IsRequired).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(cache.TryGet(new RespireKey("first"), out _)).IsFalse();
        await Assert.That(cache.TryGet(new RespireKey("second"), out _)).IsFalse();
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
        cache.CompleteMutation(in fence);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownMutationStillFlushesBeforeAndAfterCompletion()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "cached", "old");
        var command = new Cmd2(new Verb("CUSTOM.WRITE"), "key", "value");

        var fence = cache.BeforeCommand("CUSTOM.WRITE", in command);

        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.All);
        await Assert.That(cache.Count).IsEqualTo(0);
        Insert(cache, "racing", "old");
        cache.CompleteMutation(in fence);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedKnownMutationLayoutUsesUnknownMutationFence()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "cached", "old");
        var command = new CmdN(Verbs.Del, []);

        var fence = cache.BeforeCommand("DEL", in command);

        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.All);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DuplicateMultiKeyMutationKeysAreFencedOnce()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "a", "old");
        Insert(cache, "b", "old");
        Insert(cache, "unrelated", "retained");
        var before = cache.GetStatistics().Invalidations;
        var command = new CmdN(Verbs.Del, ["a", "b", "a", "b", "a"]);

        var fence = cache.BeforeCommand("DEL", in command);

        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.Keys);
        await Assert.That(fence.Keys!).IsEquivalentTo(new[] { new RespireKey("a"), new RespireKey("b") });
        await Assert.That(cache.GetStatistics().Invalidations - before).IsEqualTo(2);
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");

        cache.CompleteMutation(in fence);
        await Assert.That(cache.GetStatistics().Invalidations - before).IsEqualTo(4);
    }

    [Test]
    public async Task MultiKeyMutationCollapsingToOneDistinctKeyUsesSingleKeyFence()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var command = new CmdN(Verbs.Unlink, ["same", "same", "same"]);

        var fence = cache.BeforeCommand("UNLINK", in command);

        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.Key);
        await Assert.That(fence.Key).IsEqualTo(new RespireKey("same"));
    }

    [Test]
    public async Task LargeMultiKeyMutationDeduplicatesWithSet()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "key:0", "old");
        Insert(cache, "unrelated", "retained");
        // 2,000 arguments over 500 distinct keys exercises the hashed path above the linear-scan limit.
        var arguments = Enumerable.Range(0, 2000).Select(index => (RespireValue)$"key:{index % 500}").ToArray();
        var before = cache.GetStatistics().Invalidations;
        var command = new CmdN(Verbs.Del, arguments);

        var fence = cache.BeforeCommand("DEL", in command);

        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.Keys);
        await Assert.That(fence.Keys!.Length).IsEqualTo(500);
        await Assert.That(fence.Keys!.Distinct().Count()).IsEqualTo(500);
        await Assert.That(cache.GetStatistics().Invalidations - before).IsEqualTo(500);
        await Assert.That(cache.TryGet(new RespireKey("key:0"), out _)).IsFalse();
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
    }

    [Test]
    [Arguments("MSET", new object[] { "first", "new", "second" })]
    [Arguments("MSETNX", new object[] { "first" })]
    [Arguments("MSETEX", new object[] { 3, "first", "new", "second", "new" })]
    [Arguments("MSETEX", new object[] { 0, "first", "new" })]
    [Arguments("MSETEX", new object[] { "two", "first", "new" })]
    [Arguments("MSETEX", new object[] { })]
    [Arguments("JSON.MSET", new object[] { "first", "$", "new", "second", "$" })]
    [Arguments("UNLINK", new object[] { })]
    public async Task MalformedMultiKeyLayoutsFallBackToFullFlush(string operation, object[] raw)
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "unrelated", "old");
        var arguments = raw.Select(value => value switch
        {
            int number => (RespireValue)number,
            string text => (RespireValue)text,
            _ => throw new ArgumentOutOfRangeException(nameof(raw)),
        }).ToArray();
        var command = new CatalogCommand(RespireCommands.All.ToArray().Single(c => c.Name == operation), arguments);

        var fence = cache.BeforeCommand(operation, in command);

        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.All);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MutationLayoutsAgreeWithRoutingLayouts()
    {
        string[] operations = ["MSET", "MSETNX", "MSETEX", "DEL", "UNLINK", "JSON.MSET"];
        RespireValue[][] inputs =
        [
            [], ["a"], ["a", "b"], ["a", "b", "c"], ["a", "b", "c", "d"], ["a", "b", "c", "d", "e", "f"],
            [0], [1], [1, "a"], [1, "a", "v"], [2, "a", "v"], [2, "a", "v", "b", "v"], [2, "a", "v", "b", "v", "PX", 10],
            [3, "a", "v", "b", "v"], [-1, "a", "v"], [long.MaxValue, "a", "v"], ["x", "a", "v"],
        ];
        foreach (var operation in operations)
        foreach (var input in inputs)
        {
            RawCommandKeyLayouts.KeyLayout? expected;
            try
            {
                expected = RawCommandKeyLayouts.TryGetLayout(operation, input, out var routing) ? routing : null;
            }
            catch (ArgumentException)
            {
                expected = null;
            }

            var key = new ClientCacheCommandKey(operation, input);
            RawCommandKeyLayouts.KeyLayout? actual =
                RawCommandKeyLayouts.TryGetMutationLayout(operation, in key, out var mutation) ? mutation : null;
            await Assert.That(actual).IsEqualTo(expected)
                .Because($"{operation} [{string.Join(", ", input.Select(value => value.ToString()))}]");
        }
    }

    [Test]
    public async Task VectorReadsPreserveCacheAndMutationsFenceOnlyTheirKey()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "vectors", "old");
        Insert(cache, "unrelated", "retained");
        foreach (var operation in new[] { "VCARD", "VDIM", "VEMB", "VGETATTR", "VINFO", "VISMEMBER", "VLINKS", "VRANDMEMBER", "VRANGE", "VSIM" })
        {
            var read = new Cmd1(new Verb(operation), "vectors");
            var fence = cache.BeforeCommand(operation, in read);
            await Assert.That(fence.IsRequired).IsFalse();
            await Assert.That(cache.Count).IsEqualTo(2);
        }
        foreach (var operation in new[] { "VADD", "VREM", "VSETATTR" })
        {
            Insert(cache, "vectors", "old");
            var command = new Cmd1(new Verb(operation), "vectors");
            var fence = cache.BeforeCommand(operation, in command);
            await Assert.That(cache.Count).IsEqualTo(1);
            Insert(cache, "vectors", "racing-read");
            cache.CompleteMutation(in fence);
            await Assert.That(cache.Count).IsEqualTo(1);
            await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
        }
    }

    [Test]
    public async Task ProbabilisticMutationsFenceOnlyTheirWrittenKey()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "unrelated", "retained");
        foreach (var operation in new[]
        {
            "BF.RESERVE", "BF.ADD", "BF.MADD", "BF.INSERT", "BF.LOADCHUNK",
            "CF.RESERVE", "CF.ADD", "CF.ADDNX", "CF.INSERT", "CF.INSERTNX", "CF.DEL", "CF.LOADCHUNK",
            "CMS.INITBYDIM", "CMS.INITBYPROB", "CMS.INCRBY", "TOPK.RESERVE", "TOPK.ADD", "TOPK.INCRBY",
            "TDIGEST.CREATE", "TDIGEST.RESET", "TDIGEST.ADD",
        })
        {
            await AssertFencesOnly(operation, new Cmd1N(new Verb(operation), "sketch", ["item"]));
        }

        // Merges write only their destination; the source sketches are read.
        foreach (var operation in new[] { "CMS.MERGE", "TDIGEST.MERGE" })
        {
            Insert(cache, "source", "retained");
            await AssertFencesOnly(operation, new Cmd1N(new Verb(operation), "sketch", [1, "source"]));
            await Assert.That(Read(cache, "source")).IsEqualTo("retained");
        }

        async Task AssertFencesOnly<TCommand>(string operation, TCommand command)
            where TCommand : struct, IRespCommand
        {
            Insert(cache, "sketch", "old");
            var fence = cache.BeforeCommand(operation, in command);
            await Assert.That(fence.IsRequired).IsTrue();
            await Assert.That(cache.TryGet(new RespireKey("sketch"), out _)).IsFalse();
            cache.CompleteMutation(in fence);
            await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
        }
    }

    [Test]
    [Arguments("LPUSHX")]
    [Arguments("RPUSHX")]
    public async Task ConditionalListPushInvalidatesOnlyItsKeyBeforeAndAfterCompletion(string operation)
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "unrelated", "retained");
        var read = new Cmd1(Verbs.LLen, "list");
        await Assert.That(cache.TryCreateQuery("LLEN", in read, out var request)).IsTrue();
        CacheLength();
        await Assert.That(cache.Count).IsEqualTo(2);
        var verb = operation == "LPUSHX" ? RespireCommands.List.LPUSHX.Verb : RespireCommands.List.RPUSHX.Verb;
        var command = new Cmd1N(verb, "list", ["value"]);

        var fence = cache.BeforeCommand(operation, in command);
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");
        CacheLength();
        await Assert.That(cache.Count).IsEqualTo(2);
        cache.CompleteMutation(in fence);
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(Read(cache, "unrelated")).IsEqualTo("retained");

        void CacheLength()
        {
            var token = cache.BeginRead("LLEN", in request);
            var response = RespValue.Integer(1);
            cache.CompleteRead(in token, in response, allowInsert: true);
        }
    }

    [Test]
    public async Task Capacity_IsBoundedByEntryCount()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions
        {
            MaxEntries = 2,
            MaxSizeBytes = 1_000_000,
            TimeToLive = null,
        });

        Insert(cache, "a", "1");
        Insert(cache, "b", "2");
        Insert(cache, "c", "3");

        await Assert.That(cache.Count).IsLessThanOrEqualTo(2);
        await Assert.That(cache.GetStatistics().Evictions).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task ConcurrentInserts_DoNotEscapeEntryBound()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions
            {
                MaxEntries = 1,
                MaxSizeBytes = 1_000_000,
                TimeToLive = null,
            });

            Parallel.For(0, 32, index => Insert(cache, $"key:{index}", "value"));

            await Assert.That(cache.Count).IsLessThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task OversizedValue_IsReturnedButNotStored()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions
        {
            MaxEntries = 10,
            MaxSizeBytes = 80,
            TimeToLive = null,
        });

        Insert(cache, "key", new string('x', 100));

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExpiredEntry_IsEvictedOnAccess()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions
        {
            TimeToLive = TimeSpan.FromMilliseconds(10),
        });
        Insert(cache, "key", "value");

        await Task.Delay(30);
        var key = new RespireKey("key");

        await Assert.That(cache.TryGet(in key, out _)).IsFalse();
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BinaryKeys_DoNotCollide()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var first = new RespireKey(new byte[] { 0xFF, 0x00 });
        var second = new RespireKey(new byte[] { 0xFF, 0x01 });
        Insert(cache, first, "first");
        Insert(cache, second, "second");

        await Assert.That(Read(cache, first)).IsEqualTo("first");
        await Assert.That(Read(cache, second)).IsEqualTo("second");
    }

    [Test]
    public async Task ContinuityFlush_RejectsOlderInflightRead()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        cache.FlushForContinuityLoss();
        var response = RespValue.BulkString("stale"u8.ToArray());

        cache.CompleteRead(in token, in response, allowInsert: true);

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.GetStatistics().ContinuityFlushes).IsEqualTo(1);
    }

    [Test]
    public async Task EntryBound_IsSharedByKeyAndCommandEntries()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions
        {
            MaxEntries = 1,
            MaxSizeBytes = 1_000_000,
            TimeToLive = null,
        });
        Insert(cache, "value", "one");
        InsertQuery(cache, "length", RespValue.Integer(3));

        await Assert.That(cache.Count).IsLessThanOrEqualTo(1);
        await Assert.That(cache.GetStatistics().Evictions).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task GenericRead_IsRejectedAfterDependencyInvalidation()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var command = new Cmd1(Verbs.StrLen, "key");
        await Assert.That(cache.TryCreateQuery("STRLEN", in command, out var request)).IsTrue();
        var response = RespValue.Integer(3);
        var successfulToken = cache.BeginRead("STRLEN", in request);
        cache.CompleteRead(in successfulToken, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(1);
        cache.Clear();

        var invalidatedToken = cache.BeginRead("STRLEN", in request);
        var key = new RespireKey("key");
        cache.Invalidate(in key);

        cache.CompleteRead(in invalidatedToken, in response, allowInsert: true);

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CacheableRead_IsNeverClassifiedAsMutation()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "cached", "value");
        var command = new Cmd1(Verbs.StrLen, "key");

        var fence = cache.BeforeCommand("HSTRLEN", in command);

        await Assert.That(fence.IsRequired).IsFalse();
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RedisCacheableCommandFamilies_AreRecognized()
    {
        string[] operations =
        [
            "GET", "MGET", "STRLEN", "GETRANGE", "SUBSTR", "DIGEST", "LCS",
            "EXISTS", "EXPIRETIME", "PEXPIRETIME", "TYPE", "OBJECT ENCODING", "MEMORY USAGE",
            "HGET", "HMGET", "HGETALL", "HEXISTS", "HLEN", "HSTRLEN", "HKEYS", "HVALS",
            "HEXPIRETIME", "HPEXPIRETIME",
            "LLEN", "LRANGE", "LINDEX", "LPOS",
            "SISMEMBER", "SMISMEMBER", "SCARD", "SMEMBERS", "SINTER", "SUNION", "SDIFF",
            "SINTERCARD", "SUNIONCARD", "SDIFFCARD",
            "ZSCORE", "ZMSCORE", "ZCARD", "ZCOUNT", "ZLEXCOUNT", "ZRANK", "ZREVRANK",
            "ZRANGE", "ZRANGEBYLEX", "ZRANGEBYSCORE", "ZREVRANGE", "ZREVRANGEBYLEX",
            "ZREVRANGEBYSCORE", "ZINTER", "ZUNION", "ZDIFF", "ZINTERCARD",
            "XLEN", "XRANGE", "XREVRANGE", "XPENDING", "XINFO STREAM", "XINFO GROUPS",
            "GETBIT", "BITCOUNT", "BITPOS", "BITFIELD_RO",
            "GEODIST", "GEOHASH", "GEOPOS", "GEOSEARCH", "GEORADIUS_RO",
            "GEORADIUSBYMEMBER_RO",
            "ARCOUNT", "ARGET", "ARGETRANGE", "ARGREP", "ARINFO", "ARLASTITEMS", "ARLEN",
            "ARMGET", "ARNEXT", "AROP", "ARSCAN",
            "JSON.ARRINDEX", "JSON.ARRLEN", "JSON.GET", "JSON.MGET", "JSON.OBJKEYS",
            "JSON.OBJLEN", "JSON.RESP", "JSON.STRLEN", "JSON.TYPE",
            "VCARD", "VDIM", "VEMB", "VGETATTR", "VINFO", "VISMEMBER", "VLINKS", "VRANGE", "VSIM",
            "SORT_RO",
        ];

        foreach (var operation in operations)
        {
            await Assert.That(ClientSideCacheCoordinator.CanCacheOperation(operation))
                .IsTrue()
                .Because($"{operation} should support client-side caching");
        }
    }

    [Test]
    public async Task RedisNonCacheableCommandFamilies_AreRejected()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert(cache, "cached", "value");
        var command = new Cmd1(Verbs.StrLen, "key");
        string[] operations =
        [
            "DUMP", "TTL", "PTTL", "HTTL", "HPTTL",
            "SCAN", "HSCAN", "SSCAN", "ZSCAN", "RANDOMKEY", "HRANDFIELD", "SRANDMEMBER",
            "ZRANDMEMBER", "VRANDMEMBER", "XREAD", "EVAL_RO", "EVALSHA_RO", "FCALL_RO",
            "BF.EXISTS", "CF.EXISTS", "CMS.QUERY", "TDIGEST.CDF", "TOPK.QUERY",
            "TS.GET", "FT.SEARCH", "KEYS", "DBSIZE", "TOUCH",
        ];

        foreach (var operation in operations)
        {
            await Assert.That(ClientSideCacheCoordinator.CanCacheOperation(operation))
                .IsFalse()
                .Because($"{operation} must bypass client-side caching");
            await Assert.That(cache.BeforeCommand(operation, in command).IsRequired)
                .IsFalse()
                .Because($"{operation} is read-only and must not create a mutation fence");
        }

        await Assert.That(ClientSideCacheCoordinator.CanCacheOperation("PFCOUNT")).IsFalse();
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsonMGet_InvalidatesOnEveryDocumentKey()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var command = new CatalogCommand(
            RespireCommands.Json.JSON_MGET,
            ["first", "second", "$"]);
        await Assert.That(cache.TryCreateQuery("JSON.MGET", in command, out var request)).IsTrue();
        var token = cache.BeginRead("JSON.MGET", in request);
        var response = RespValue.Array([]);
        cache.CompleteRead(in token, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(1);

        var second = new RespireKey("second");
        cache.Invalidate(in second);

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RawGeoSearchAny_IsNotCacheable()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var deterministic = new CatalogCommand(
            RespireCommands.Geo.GEOSEARCH,
            ["places", "FROMMEMBER", "origin", "BYRADIUS", 1, "m", "COUNT", 1]);
        var any = new CatalogCommand(
            RespireCommands.Geo.GEOSEARCH,
            ["places", "FROMMEMBER", "origin", "BYRADIUS", 1, "m", "COUNT", 1, "ANY"]);

        await Assert.That(cache.TryCreateQuery("GEOSEARCH", in deterministic, out _)).IsTrue();
        await Assert.That(cache.TryCreateQuery("GEOSEARCH", in any, out _)).IsFalse();
    }

    [Test]
    public async Task OnlyExactMemoryUsage_IsCacheable()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var defaultSampling = new CatalogCommand(RespireCommands.Server.MEMORY_USAGE, ["key"]);
        var sampled = new CatalogCommand(
            RespireCommands.Server.MEMORY_USAGE, ["key", "SAMPLES", 5]);
        var exact = new CatalogCommand(
            RespireCommands.Server.MEMORY_USAGE, ["key", "SAMPLES", 0]);

        await Assert.That(cache.TryCreateQuery("MEMORY USAGE", in defaultSampling, out _)).IsFalse();
        await Assert.That(cache.TryCreateQuery("MEMORY USAGE", in sampled, out _)).IsFalse();
        await Assert.That(cache.TryCreateQuery("MEMORY USAGE", in exact, out _)).IsTrue();
    }

    [Test]
    public async Task SortReadOnly_RejectsImplicitExternalKeyPatterns()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var selfContained = new CatalogCommand(RespireCommands.Key.SORT_RO, ["key", "ALPHA"]);
        var external = new CatalogCommand(RespireCommands.Key.SORT_RO, ["key", "BY", "weight_*"]);

        await Assert.That(cache.TryCreateQuery("SORT_RO", in selfContained, out _)).IsTrue();
        await Assert.That(cache.TryCreateQuery("SORT_RO", in external, out _)).IsFalse();
    }

    private static void Insert(ClientSideCacheCoordinator cache, string key, string value)
        => Insert(cache, new RespireKey(key), value);

    private static void Insert(ClientSideCacheCoordinator cache, RespireKey key, string value)
    {
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString(System.Text.Encoding.UTF8.GetBytes(value));
        cache.CompleteRead(in token, in response, allowInsert: true);
    }

    private static string Read(ClientSideCacheCoordinator cache, RespireKey key)
    {
        if (!cache.TryGet(in key, out var response))
        {
            throw new InvalidOperationException("Expected cached value.");
        }

        return response.AsString();
    }

    private static void InsertQuery(
        ClientSideCacheCoordinator cache,
        RespireValue key,
        RespValue response)
    {
        var command = new Cmd1(Verbs.StrLen, key);
        if (!cache.TryCreateQuery("STRLEN", in command, out var request))
        {
            throw new InvalidOperationException("Expected a cacheable query.");
        }

        var token = cache.BeginRead("STRLEN", in request);
        cache.CompleteRead(in token, in response, allowInsert: true);
    }
}
