using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using DotNet.Testcontainers.Containers;
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
    private const int SteadyStateOps = 100;
    private const int PipelinedOps = 200;

    private IContainer? _redisContainer;
    private RespireClient _respire = null!;
    private ConnectionMultiplexer _stackExchange = null!;
    private IDatabase _stackExchangeDb = null!;

    private readonly string _smallValue = "Hello, World!";
    private readonly string _1KBValue = new('X', 1024);
    private readonly string _10KBValue = new('Y', 10240);
    private readonly Task<RedisValue>[] _stackExchangeConcurrent = new Task<RedisValue>[ConcurrentOps];
    private readonly ValueTask<string?>[] _respireConcurrent = new ValueTask<string?>[ConcurrentOps];
    private readonly Task<RedisValue>[] _stackExchangePipelined = new Task<RedisValue>[PipelinedOps];
    private readonly ValueTask<string?>[] _respirePipelined = new ValueTask<string?>[PipelinedOps];

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

        _respire = await RespireClient.ConnectAsync($"{host}:{port}");
        _stackExchange = await ConnectionMultiplexer.ConnectAsync($"{host}:{port}");
        _stackExchangeDb = _stackExchange.GetDatabase();

        await _respire.SetAsync("seeded:string", _smallValue);
        await _respire.Hashes.SetAsync("seeded:hash", "field", _smallValue);
        await _respire.Sets.AddAsync("seeded:set", "member");

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
    public Task<TimeSpan> StackExchange_Ping() => _stackExchangeDb.PingAsync();

    [Benchmark, BenchmarkCategory("PING")]
    public ValueTask<TimeSpan> Respire_Ping() => _respire.PingAsync();

    // GET (value read as a string, as a typical caller would)

    [Benchmark(Baseline = true), BenchmarkCategory("GET")]
    public async Task<string?> StackExchange_Get() => await _stackExchangeDb.StringGetAsync("seeded:string");

    [Benchmark, BenchmarkCategory("GET")]
    public ValueTask<string?> Respire_Get() => _respire.GetStringAsync("seeded:string");

    // SET

    [Benchmark(Baseline = true), BenchmarkCategory("SET 13B")]
    public Task<bool> StackExchange_Set_Small() => _stackExchangeDb.StringSetAsync("bench:set", _smallValue);

    [Benchmark, BenchmarkCategory("SET 13B")]
    public ValueTask<bool> Respire_Set_Small() => _respire.SetAsync("bench:set", _smallValue);

    [Benchmark(Baseline = true), BenchmarkCategory("SET 1KB")]
    public Task<bool> StackExchange_Set_1KB() => _stackExchangeDb.StringSetAsync("bench:set1kb", _1KBValue);

    [Benchmark, BenchmarkCategory("SET 1KB")]
    public ValueTask<bool> Respire_Set_1KB() => _respire.SetAsync("bench:set1kb", _1KBValue);

    [Benchmark(Baseline = true), BenchmarkCategory("SET 10KB")]
    public Task<bool> StackExchange_Set_10KB() => _stackExchangeDb.StringSetAsync("bench:set10kb", _10KBValue);

    [Benchmark, BenchmarkCategory("SET 10KB")]
    public ValueTask<bool> Respire_Set_10KB() => _respire.SetAsync("bench:set10kb", _10KBValue);

    // Batched sequential operations amortize BenchmarkDotNet's async invocation adapter,
    // exposing each client's steady-state allocation rather than benchmark harness overhead.

    [Benchmark(Baseline = true, OperationsPerInvoke = SteadyStateOps), BenchmarkCategory("PING x100 sequential")]
    public async Task<TimeSpan> StackExchange_Ping_SteadyState()
    {
        var result = default(TimeSpan);
        for (var i = 0; i < SteadyStateOps; i++)
        {
            result = await _stackExchangeDb.PingAsync();
        }

        return result;
    }

    [Benchmark(OperationsPerInvoke = SteadyStateOps), BenchmarkCategory("PING x100 sequential")]
    public async Task<TimeSpan> Respire_Ping_SteadyState()
    {
        var result = default(TimeSpan);
        for (var i = 0; i < SteadyStateOps; i++)
        {
            result = await _respire.PingAsync();
        }

        return result;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = SteadyStateOps), BenchmarkCategory("GET x100 sequential")]
    public async Task<string?> StackExchange_Get_SteadyState()
    {
        string? result = null;
        for (var i = 0; i < SteadyStateOps; i++)
        {
            result = await _stackExchangeDb.StringGetAsync("seeded:string");
        }

        return result;
    }

    [Benchmark(OperationsPerInvoke = SteadyStateOps), BenchmarkCategory("GET x100 sequential")]
    public async Task<string?> Respire_Get_SteadyState()
    {
        string? result = null;
        for (var i = 0; i < SteadyStateOps; i++)
        {
            result = await _respire.GetStringAsync("seeded:string");
        }

        return result;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = SteadyStateOps), BenchmarkCategory("SET x100 sequential")]
    public async Task<bool> StackExchange_Set_SteadyState()
    {
        var result = false;
        for (var i = 0; i < SteadyStateOps; i++)
        {
            result = await _stackExchangeDb.StringSetAsync("bench:set", _smallValue);
        }

        return result;
    }

    [Benchmark(OperationsPerInvoke = SteadyStateOps), BenchmarkCategory("SET x100 sequential")]
    public async Task<bool> Respire_Set_SteadyState()
    {
        var result = false;
        for (var i = 0; i < SteadyStateOps; i++)
        {
            result = await _respire.SetAsync("bench:set", _smallValue);
        }

        return result;
    }

    // INCR

    [Benchmark(Baseline = true), BenchmarkCategory("INCR")]
    public Task<long> StackExchange_Incr() => _stackExchangeDb.StringIncrementAsync("bench:counter");

    [Benchmark, BenchmarkCategory("INCR")]
    public ValueTask<long> Respire_Incr() => _respire.IncrementAsync("bench:counter");

    // EXISTS

    [Benchmark(Baseline = true), BenchmarkCategory("EXISTS")]
    public Task<bool> StackExchange_Exists() => _stackExchangeDb.KeyExistsAsync("seeded:string");

    [Benchmark, BenchmarkCategory("EXISTS")]
    public ValueTask<bool> Respire_Exists() => _respire.ExistsAsync("seeded:string");

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
        return await _respire.DeleteAsync("bench:del");
    }

    // HSET / HGET

    [Benchmark(Baseline = true), BenchmarkCategory("HSET")]
    public Task<bool> StackExchange_HSet() => _stackExchangeDb.HashSetAsync("bench:hash", "field", _smallValue);

    [Benchmark, BenchmarkCategory("HSET")]
    public ValueTask<bool> Respire_HSet() => _respire.Hashes.SetAsync("bench:hash", "field", _smallValue);

    [Benchmark(Baseline = true), BenchmarkCategory("HGET")]
    public async Task<string?> StackExchange_HGet() => await _stackExchangeDb.HashGetAsync("seeded:hash", "field");

    [Benchmark, BenchmarkCategory("HGET")]
    public ValueTask<string?> Respire_HGet() => _respire.Hashes.GetStringAsync("seeded:hash", "field");

    // LPUSH + LPOP (paired so the list stays a constant size)

    [Benchmark(Baseline = true), BenchmarkCategory("LPUSH+LPOP")]
    public async Task<string?> StackExchange_LPushLPop()
    {
        await _stackExchangeDb.ListLeftPushAsync("bench:list", _smallValue);
        return await _stackExchangeDb.ListLeftPopAsync("bench:list");
    }

    [Benchmark, BenchmarkCategory("LPUSH+LPOP")]
    public async Task<string?> Respire_LPushLPop()
    {
        await _respire.Lists.LeftPushAsync("bench:list", _smallValue);
        return await _respire.Lists.LeftPopAsync("bench:list");
    }

    // SADD (same member every time, so set size stays constant)

    [Benchmark(Baseline = true), BenchmarkCategory("SADD")]
    public Task<bool> StackExchange_SAdd() => _stackExchangeDb.SetAddAsync("seeded:set", "member");

    [Benchmark, BenchmarkCategory("SADD")]
    public ValueTask<long> Respire_SAdd() => _respire.Sets.AddAsync("seeded:set", "member");

    // SISMEMBER

    [Benchmark(Baseline = true), BenchmarkCategory("SISMEMBER")]
    public Task<bool> StackExchange_SIsMember() => _stackExchangeDb.SetContainsAsync("seeded:set", "member");

    [Benchmark, BenchmarkCategory("SISMEMBER")]
    public ValueTask<bool> Respire_SIsMember() => _respire.Sets.ContainsAsync("seeded:set", "member");

    // Concurrent GETs — 50 overlapping requests per invocation; reported per operation

    [Benchmark(Baseline = true, OperationsPerInvoke = ConcurrentOps), BenchmarkCategory("GET x50 concurrent")]
    public async Task StackExchange_Get_Concurrent()
    {
        for (var i = 0; i < ConcurrentOps; i++)
        {
            _stackExchangeConcurrent[i] = _stackExchangeDb.StringGetAsync("seeded:string");
        }

        for (var i = 0; i < _stackExchangeConcurrent.Length; i++)
        {
            await _stackExchangeConcurrent[i];
        }
    }

    [Benchmark(OperationsPerInvoke = ConcurrentOps), BenchmarkCategory("GET x50 concurrent")]
    public async Task Respire_Get_Concurrent()
    {
        for (var i = 0; i < ConcurrentOps; i++)
        {
            _respireConcurrent[i] = _respire.GetStringAsync("seeded:string");
        }

        for (var i = 0; i < _respireConcurrent.Length; i++)
        {
            await _respireConcurrent[i];
        }
    }

    // Deeply pipelined GETs — 200 overlapping requests amortize round-trip latency far enough
    // that per-response CPU (parse, complete, decode) dominates the measurement.

    [Benchmark(Baseline = true, OperationsPerInvoke = PipelinedOps), BenchmarkCategory("GET x200 pipelined")]
    public async Task StackExchange_Get_Pipelined()
    {
        for (var i = 0; i < PipelinedOps; i++)
        {
            _stackExchangePipelined[i] = _stackExchangeDb.StringGetAsync("seeded:string");
        }

        for (var i = 0; i < _stackExchangePipelined.Length; i++)
        {
            await _stackExchangePipelined[i];
        }
    }

    [Benchmark(OperationsPerInvoke = PipelinedOps), BenchmarkCategory("GET x200 pipelined")]
    public async Task Respire_Get_Pipelined()
    {
        for (var i = 0; i < PipelinedOps; i++)
        {
            _respirePipelined[i] = _respire.GetStringAsync("seeded:string");
        }

        for (var i = 0; i < _respirePipelined.Length; i++)
        {
            await _respirePipelined[i];
        }
    }
}
