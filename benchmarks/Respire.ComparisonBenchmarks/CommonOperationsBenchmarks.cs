using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using DotNet.Testcontainers.Containers;
using Respire.FastClient;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Respire.ComparisonBenchmarks;

/// <summary>
/// Head-to-head comparison of Respire and StackExchange.Redis across common Redis operations.
/// Benchmarks are grouped by operation; within each group StackExchange.Redis is the baseline,
/// so a Ratio below 1.00 means Respire is faster for that operation.
/// </summary>
/// <remarks>
/// The Redis endpoint is taken from the REDIS_HOST / REDIS_PORT environment variables when set
/// (as in CI, where a Redis service container is provided); otherwise a throwaway Redis
/// Testcontainer is started for the duration of the run.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CommonOperationsBenchmarks
{
    private const int ConcurrentOps = 50;

    private IContainer? _redisContainer;
    private RespireClient _respire = null!;
    private ConnectionMultiplexer _stackExchange = null!;
    private IDatabase _stackExchangeDb = null!;

    private readonly string _smallValue = "Hello, World!";
    private readonly string _1KBValue = new('X', 1024);
    private readonly string _10KBValue = new('Y', 10240);

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        int port;

        if (string.IsNullOrEmpty(host))
        {
            _redisContainer = new RedisBuilder("redis:8.0").Build();
            await _redisContainer.StartAsync();
            host = "localhost";
            port = _redisContainer.GetMappedPublicPort(6379);
        }
        else
        {
            port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var p) ? p : 6379;
        }

        _respire = await RespireClient.CreateAsync(host, port);
        _stackExchange = await ConnectionMultiplexer.ConnectAsync($"{host}:{port}");
        _stackExchangeDb = _stackExchange.GetDatabase();

        await _respire.SetAsync("seeded:string", _smallValue);
        await _respire.HSetAsync("seeded:hash", "field", _smallValue);
        await _respire.SAddAsync("seeded:set", "member");

        for (var i = 0; i < 10; i++)
        {
            await _respire.PingAsync();
            await _stackExchangeDb.PingAsync();
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _respire.DisposeAsync();
        await _stackExchange.DisposeAsync();

        if (_redisContainer is not null)
        {
            await _redisContainer.DisposeAsync();
        }
    }

    // PING

    [Benchmark(Baseline = true), BenchmarkCategory("PING")]
    public async Task<TimeSpan> StackExchange_Ping() => await _stackExchangeDb.PingAsync();

    [Benchmark, BenchmarkCategory("PING")]
    public async Task<string> Respire_Ping() => await _respire.PingAsync();

    // GET (value read as a string, as a typical caller would)

    [Benchmark(Baseline = true), BenchmarkCategory("GET")]
    public async Task<string?> StackExchange_Get() => await _stackExchangeDb.StringGetAsync("seeded:string");

    [Benchmark, BenchmarkCategory("GET")]
    public async Task<string> Respire_Get()
    {
        var value = await _respire.GetAsync("seeded:string");
        var result = value.AsString();
        value.Dispose();
        return result;
    }

    // SET

    [Benchmark(Baseline = true), BenchmarkCategory("SET 13B")]
    public async Task<bool> StackExchange_Set_Small() => await _stackExchangeDb.StringSetAsync("bench:set", _smallValue);

    [Benchmark, BenchmarkCategory("SET 13B")]
    public async Task Respire_Set_Small() => await _respire.SetAsync("bench:set", _smallValue);

    [Benchmark(Baseline = true), BenchmarkCategory("SET 1KB")]
    public async Task<bool> StackExchange_Set_1KB() => await _stackExchangeDb.StringSetAsync("bench:set1kb", _1KBValue);

    [Benchmark, BenchmarkCategory("SET 1KB")]
    public async Task Respire_Set_1KB() => await _respire.SetAsync("bench:set1kb", _1KBValue);

    [Benchmark(Baseline = true), BenchmarkCategory("SET 10KB")]
    public async Task<bool> StackExchange_Set_10KB() => await _stackExchangeDb.StringSetAsync("bench:set10kb", _10KBValue);

    [Benchmark, BenchmarkCategory("SET 10KB")]
    public async Task Respire_Set_10KB() => await _respire.SetAsync("bench:set10kb", _10KBValue);

    // INCR

    [Benchmark(Baseline = true), BenchmarkCategory("INCR")]
    public async Task<long> StackExchange_Incr() => await _stackExchangeDb.StringIncrementAsync("bench:counter");

    [Benchmark, BenchmarkCategory("INCR")]
    public async Task<long> Respire_Incr() => await _respire.IncrAsync("bench:counter");

    // EXISTS

    [Benchmark(Baseline = true), BenchmarkCategory("EXISTS")]
    public async Task<bool> StackExchange_Exists() => await _stackExchangeDb.KeyExistsAsync("seeded:string");

    [Benchmark, BenchmarkCategory("EXISTS")]
    public async Task<bool> Respire_Exists() => await _respire.ExistsAsync("seeded:string");

    // SET + DEL (paired so the key always exists when deleted)

    [Benchmark(Baseline = true), BenchmarkCategory("SET+DEL")]
    public async Task<bool> StackExchange_SetDel()
    {
        await _stackExchangeDb.StringSetAsync("bench:del", _smallValue);
        return await _stackExchangeDb.KeyDeleteAsync("bench:del");
    }

    [Benchmark, BenchmarkCategory("SET+DEL")]
    public async Task<long> Respire_SetDel()
    {
        await _respire.SetAsync("bench:del", _smallValue);
        return await _respire.DelAsync("bench:del");
    }

    // HSET / HGET

    [Benchmark(Baseline = true), BenchmarkCategory("HSET")]
    public async Task<bool> StackExchange_HSet() => await _stackExchangeDb.HashSetAsync("bench:hash", "field", _smallValue);

    [Benchmark, BenchmarkCategory("HSET")]
    public async Task<long> Respire_HSet() => await _respire.HSetAsync("bench:hash", "field", _smallValue);

    [Benchmark(Baseline = true), BenchmarkCategory("HGET")]
    public async Task<string?> StackExchange_HGet() => await _stackExchangeDb.HashGetAsync("seeded:hash", "field");

    [Benchmark, BenchmarkCategory("HGET")]
    public async Task<string> Respire_HGet()
    {
        var value = await _respire.HGetAsync("seeded:hash", "field");
        var result = value.AsString();
        value.Dispose();
        return result;
    }

    // LPUSH + LPOP (paired so the list stays a constant size)

    [Benchmark(Baseline = true), BenchmarkCategory("LPUSH+LPOP")]
    public async Task<string?> StackExchange_LPushLPop()
    {
        await _stackExchangeDb.ListLeftPushAsync("bench:list", _smallValue);
        return await _stackExchangeDb.ListLeftPopAsync("bench:list");
    }

    [Benchmark, BenchmarkCategory("LPUSH+LPOP")]
    public async Task<string> Respire_LPushLPop()
    {
        await _respire.LPushAsync("bench:list", _smallValue);
        var value = await _respire.LPopAsync("bench:list");
        var result = value.AsString();
        value.Dispose();
        return result;
    }

    // SADD (same member every time, so set size stays constant)

    [Benchmark(Baseline = true), BenchmarkCategory("SADD")]
    public async Task<bool> StackExchange_SAdd() => await _stackExchangeDb.SetAddAsync("seeded:set", "member");

    [Benchmark, BenchmarkCategory("SADD")]
    public async Task<long> Respire_SAdd() => await _respire.SAddAsync("seeded:set", "member");

    // SISMEMBER

    [Benchmark(Baseline = true), BenchmarkCategory("SISMEMBER")]
    public async Task<bool> StackExchange_SIsMember() => await _stackExchangeDb.SetContainsAsync("seeded:set", "member");

    [Benchmark, BenchmarkCategory("SISMEMBER")]
    public async Task<bool> Respire_SIsMember() => await _respire.SIsMemberAsync("seeded:set", "member");

    // Concurrent GETs — 50 overlapping requests per invocation; reported per operation

    [Benchmark(Baseline = true, OperationsPerInvoke = ConcurrentOps), BenchmarkCategory("GET x50 concurrent")]
    public async Task StackExchange_Get_Concurrent()
    {
        var tasks = new Task[ConcurrentOps];
        for (var i = 0; i < ConcurrentOps; i++)
        {
            tasks[i] = _stackExchangeDb.StringGetAsync("seeded:string");
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark(OperationsPerInvoke = ConcurrentOps), BenchmarkCategory("GET x50 concurrent")]
    public async Task Respire_Get_Concurrent()
    {
        var tasks = new Task[ConcurrentOps];
        for (var i = 0; i < ConcurrentOps; i++)
        {
            tasks[i] = RespireGetOneAsync();
        }

        await Task.WhenAll(tasks);
    }

    private async Task RespireGetOneAsync()
    {
        var value = await _respire.GetAsync("seeded:string");
        value.Dispose();
    }
}
