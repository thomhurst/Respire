using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Valkey.Glide;
using static Valkey.Glide.ConnectionConfiguration;

namespace Respire.GlideComparisonBenchmarks;

/// <summary>
/// Head-to-head comparison of Respire and Valkey GLIDE across common operations.
/// Benchmarks are grouped by operation; within each group Valkey GLIDE is the baseline,
/// so a Ratio below 1.00 means Respire is faster for that operation.
/// </summary>
/// <remarks>
/// <para>
/// The server endpoint is taken from the REDIS_HOST / REDIS_PORT environment variables when set
/// (as in CI, where a Valkey service container is provided); otherwise a throwaway Valkey
/// container is started for the duration of the run.
/// </para>
/// <para>
/// GLIDE uses its native <see cref="GlideClient"/> API with default configuration (one multiplexed
/// connection, RESP3). Respire also uses its default configuration. Both clients decode string
/// replies to <see cref="string"/>, as a typical caller would.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class GlideCommonOperationsBenchmarks
{
    private const string ValkeyImage = "valkey/valkey:9.0";
    private const int ConcurrentOps = 50;
    private const int SteadyStateOps = 100;
    private const int PipelinedOps = 200;

    private IContainer? _container;
    private RespireClient _respire = null!;
    private GlideClient _glide = null!;

    private readonly string _smallValue = "Hello, World!";
    private readonly string _1KBValue = new('X', 1024);
    private readonly string _10KBValue = new('Y', 10240);
    private readonly Task<ValkeyValue>[] _glideConcurrent = new Task<ValkeyValue>[ConcurrentOps];
    private readonly ValueTask<string?>[] _respireConcurrent = new ValueTask<string?>[ConcurrentOps];
    private readonly Task<ValkeyValue>[] _glidePipelined = new Task<ValkeyValue>[PipelinedOps];
    private readonly ValueTask<string?>[] _respirePipelined = new ValueTask<string?>[PipelinedOps];

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        ushort port;

        if (string.IsNullOrEmpty(host))
        {
            _container = new ContainerBuilder(ValkeyImage)
                .WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("valkey-cli", "ping"))
                .Build();
            await _container.StartAsync();
            host = "localhost";
            port = _container.GetMappedPublicPort(6379);
        }
        else
        {
            port = ushort.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var p) ? p : (ushort)6379;
        }

        _respire = await RespireClient.ConnectAsync($"{host}:{port}");
        _glide = await GlideClient.CreateClient(
            new StandaloneClientConfigurationBuilder()
                .WithAddress(host, port)
                .Build());

        await _respire.SetAsync("seeded:string", _smallValue);
        await _respire.SetAsync("seeded:integer", 123456789L);
        await _respire.Hashes.SetAsync("seeded:hash", "field", _smallValue);
        await _respire.Sets.AddAsync("seeded:set", "member");

        // Positive control: both clients must read the seeded values before anything is measured.
        Verify(await _respire.GetStringAsync("seeded:string"), "Respire GET");
        Verify((string?)await _glide.GetAsync("seeded:string"), "GLIDE GET");
        Verify((string?)await _glide.HashGetAsync("seeded:hash", "field"), "GLIDE HGET");

        for (var i = 0; i < 10; i++)
        {
            await _respire.PingAsync();
            await _glide.PingAsync();
        }
    }

    private void Verify(string? actual, string operation)
    {
        if (actual != _smallValue)
        {
            throw new InvalidOperationException($"{operation} returned '{actual}', expected '{_smallValue}'.");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _respire.DisposeAsync();
        _glide.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    // PING

    [Benchmark(Baseline = true), BenchmarkCategory("PING")]
    public Task<ValkeyValue> Glide_Ping() => _glide.PingAsync();

    [Benchmark, BenchmarkCategory("PING")]
    public ValueTask<TimeSpan> Respire_Ping() => _respire.PingAsync();

    // GET (value read as a string, as a typical caller would)

    [Benchmark(Baseline = true), BenchmarkCategory("GET")]
    public async Task<string?> Glide_Get() => await _glide.GetAsync("seeded:string");

    [Benchmark, BenchmarkCategory("GET")]
    public ValueTask<string?> Respire_Get() => _respire.GetStringAsync("seeded:string");

    [Benchmark(Baseline = true), BenchmarkCategory("GET long")]
    public async Task<long> Glide_GetLong() => (long)await _glide.GetAsync("seeded:integer");

    [Benchmark, BenchmarkCategory("GET long")]
    public ValueTask<long> Respire_GetLong() => _respire.GetAsync<long>("seeded:integer");

    // SET

    [Benchmark(Baseline = true), BenchmarkCategory("SET 13B")]
    public Task Glide_Set_Small() => _glide.SetAsync("bench:set", _smallValue);

    [Benchmark, BenchmarkCategory("SET 13B")]
    public ValueTask<bool> Respire_Set_Small() => _respire.SetAsync("bench:set", _smallValue);

    [Benchmark(Baseline = true), BenchmarkCategory("SET 1KB")]
    public Task Glide_Set_1KB() => _glide.SetAsync("bench:set1kb", _1KBValue);

    [Benchmark, BenchmarkCategory("SET 1KB")]
    public ValueTask<bool> Respire_Set_1KB() => _respire.SetAsync("bench:set1kb", _1KBValue);

    [Benchmark(Baseline = true), BenchmarkCategory("SET 10KB")]
    public Task Glide_Set_10KB() => _glide.SetAsync("bench:set10kb", _10KBValue);

    [Benchmark, BenchmarkCategory("SET 10KB")]
    public ValueTask<bool> Respire_Set_10KB() => _respire.SetAsync("bench:set10kb", _10KBValue);

    // Batched sequential operations amortize BenchmarkDotNet's async invocation adapter,
    // exposing each client's steady-state cost rather than benchmark harness overhead.

    [Benchmark(Baseline = true, OperationsPerInvoke = SteadyStateOps), BenchmarkCategory("GET x100 sequential")]
    public async Task<string?> Glide_Get_SteadyState()
    {
        string? result = null;
        for (var i = 0; i < SteadyStateOps; i++)
        {
            result = await _glide.GetAsync("seeded:string");
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
    public async Task Glide_Set_SteadyState()
    {
        // GLIDE's SET returns no result; it throws when the command fails.
        for (var i = 0; i < SteadyStateOps; i++)
        {
            await _glide.SetAsync("bench:set", _smallValue);
        }
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
    public Task<long> Glide_Incr() => _glide.IncrementAsync("bench:counter", 1L);

    [Benchmark, BenchmarkCategory("INCR")]
    public ValueTask<long> Respire_Incr() => _respire.IncrementAsync("bench:counter");

    // EXISTS

    [Benchmark(Baseline = true), BenchmarkCategory("EXISTS")]
    public Task<bool> Glide_Exists() => _glide.ExistsAsync("seeded:string");

    [Benchmark, BenchmarkCategory("EXISTS")]
    public ValueTask<bool> Respire_Exists() => _respire.ExistsAsync("seeded:string");

    // HSET / HGET

    [Benchmark(Baseline = true), BenchmarkCategory("HSET")]
    public Task<bool> Glide_HSet() => _glide.HashSetAsync("bench:hash", "field", _smallValue);

    [Benchmark, BenchmarkCategory("HSET")]
    public ValueTask<bool> Respire_HSet() => _respire.Hashes.SetAsync("bench:hash", "field", _smallValue);

    [Benchmark(Baseline = true), BenchmarkCategory("HGET")]
    public async Task<string?> Glide_HGet() => await _glide.HashGetAsync("seeded:hash", "field");

    [Benchmark, BenchmarkCategory("HGET")]
    public ValueTask<string?> Respire_HGet() => _respire.Hashes.GetStringAsync("seeded:hash", "field");

    // LPUSH + LPOP (paired so the list stays a constant size)

    [Benchmark(Baseline = true), BenchmarkCategory("LPUSH+LPOP")]
    public async Task<string?> Glide_LPushLPop()
    {
        await _glide.ListLeftPushAsync("bench:list", _smallValue);
        return await _glide.ListLeftPopAsync("bench:list");
    }

    [Benchmark, BenchmarkCategory("LPUSH+LPOP")]
    public async Task<string?> Respire_LPushLPop()
    {
        await _respire.Lists.LeftPushAsync("bench:list", _smallValue);
        return await _respire.Lists.LeftPopAsync("bench:list");
    }

    // SADD (same member every time, so set size stays constant)

    [Benchmark(Baseline = true), BenchmarkCategory("SADD")]
    public Task<bool> Glide_SAdd() => _glide.SetAddAsync("seeded:set", "member");

    [Benchmark, BenchmarkCategory("SADD")]
    public ValueTask<long> Respire_SAdd() => _respire.Sets.AddAsync("seeded:set", "member");

    // SISMEMBER

    [Benchmark(Baseline = true), BenchmarkCategory("SISMEMBER")]
    public Task<bool> Glide_SIsMember() => _glide.SetIsMemberAsync("seeded:set", "member");

    [Benchmark, BenchmarkCategory("SISMEMBER")]
    public ValueTask<bool> Respire_SIsMember() => _respire.Sets.ContainsAsync("seeded:set", "member");

    // Concurrent GETs: 50 overlapping requests per invocation, reported per operation.

    [Benchmark(Baseline = true, OperationsPerInvoke = ConcurrentOps), BenchmarkCategory("GET x50 concurrent")]
    public async Task Glide_Get_Concurrent()
    {
        for (var i = 0; i < ConcurrentOps; i++)
        {
            _glideConcurrent[i] = _glide.GetAsync("seeded:string");
        }

        for (var i = 0; i < _glideConcurrent.Length; i++)
        {
            _ = (string?)await _glideConcurrent[i];
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

    // Deeply pipelined GETs: 200 overlapping requests amortize round-trip latency far enough
    // that per-response client CPU dominates the measurement.

    [Benchmark(Baseline = true, OperationsPerInvoke = PipelinedOps), BenchmarkCategory("GET x200 pipelined")]
    public async Task Glide_Get_Pipelined()
    {
        for (var i = 0; i < PipelinedOps; i++)
        {
            _glidePipelined[i] = _glide.GetAsync("seeded:string");
        }

        for (var i = 0; i < _glidePipelined.Length; i++)
        {
            _ = (string?)await _glidePipelined[i];
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
