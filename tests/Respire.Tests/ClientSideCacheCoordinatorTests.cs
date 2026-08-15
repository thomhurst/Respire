using Respire.Protocol;
using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientSideCacheCoordinatorTests
{
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
