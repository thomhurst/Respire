using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Respire.ComparisonBenchmarks;

/// <summary>
/// Compares Respire client-cache hits with ordinary server reads from Respire and
/// StackExchange.Redis. StackExchange.Redis does not provide an equivalent
/// server-assisted local-cache API, so its methods are network-read baselines.
/// </summary>
/// <remarks>
/// The Redis endpoint is taken from the REDIS_HOST / REDIS_PORT environment variables when set;
/// otherwise a throwaway Redis Testcontainer is started for the duration of the run.
/// </remarks>
[MemoryDiagnoser]
[OperationsPerSecond]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ClientSideCachingBenchmarks
{
    private const string ExistingKey = "cache:existing";
    private const string MissingKey = "cache:missing";
    private const string HashKey = "cache:hash";
    private const string HashField = "field";
    private const string Value = "Hello, client cache!";

    private IContainer? _redisContainer;
    private RespireClient _respire = null!;
    private RespireClient _respireCached = null!;
    private ConnectionMultiplexer _stackExchange = null!;
    private IDatabase _stackExchangeDb = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        int port;

        if (string.IsNullOrEmpty(host))
        {
            _redisContainer = new RedisBuilder("redis:8.10").Build();
            await _redisContainer.StartAsync();
            host = "localhost";
            port = _redisContainer.GetMappedPublicPort(6379);
        }
        else
        {
            port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort)
                ? configuredPort
                : 6379;
        }

        var connectionString = $"{host}:{port}";
        _respire = await RespireClient.ConnectAsync(connectionString);
        _respireCached = await RespireClient.ConnectAsync(
            RespireOptions.Parse(connectionString) with { ClientSideCache = new() });
        _stackExchange = await ConnectionMultiplexer.ConnectAsync(connectionString);
        _stackExchangeDb = _stackExchange.GetDatabase();

        await _stackExchangeDb.StringSetAsync(ExistingKey, Value);
        await _stackExchangeDb.KeyDeleteAsync(MissingKey);
        await _stackExchangeDb.HashSetAsync(HashKey, HashField, Value);

        await _respireCached.GetStringAsync(ExistingKey);
        await _respireCached.GetStringAsync(MissingKey);
        await _respireCached.Hashes.GetStringAsync(HashKey, HashField);
        await _respireCached.ExistsAsync(ExistingKey);

        // Verify every measured cached operation takes its local-hit path before timing starts.
        await _respireCached.GetStringAsync(ExistingKey);
        await _respireCached.GetStringAsync(MissingKey);
        await _respireCached.Hashes.GetStringAsync(HashKey, HashField);
        await _respireCached.ExistsAsync(ExistingKey);

        if (_respireCached.ClientSideCache?.GetStatistics().Hits != 4)
        {
            throw new InvalidOperationException("Failed to prime Respire client-cache benchmark entries.");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _respire.DisposeAsync();
        await _respireCached.DisposeAsync();
        await _stackExchange.DisposeAsync();

        if (_redisContainer is not null)
        {
            await _redisContainer.DisposeAsync();
        }
    }

    // Existing GET: optimized per-key value-cache path.

    [Benchmark(Baseline = true), BenchmarkCategory("GET hot")]
    public async Task<string?> StackExchange_Get_ServerRead() =>
        await _stackExchangeDb.StringGetAsync(ExistingKey);

    [Benchmark, BenchmarkCategory("GET hot")]
    public ValueTask<string?> Respire_Get_ServerRead() => _respire.GetStringAsync(ExistingKey);

    [Benchmark, BenchmarkCategory("GET hot")]
    public ValueTask<string?> Respire_Get_ClientCacheHit() => _respireCached.GetStringAsync(ExistingKey);

    // Missing GET: negative-cache path.

    [Benchmark(Baseline = true), BenchmarkCategory("GET missing hot")]
    public async Task<string?> StackExchange_GetMissing_ServerRead() =>
        await _stackExchangeDb.StringGetAsync(MissingKey);

    [Benchmark, BenchmarkCategory("GET missing hot")]
    public ValueTask<string?> Respire_GetMissing_ServerRead() => _respire.GetStringAsync(MissingKey);

    [Benchmark, BenchmarkCategory("GET missing hot")]
    public ValueTask<string?> Respire_GetMissing_ClientCacheHit() => _respireCached.GetStringAsync(MissingKey);

    // HGET: canonical command-query cache path.

    [Benchmark(Baseline = true), BenchmarkCategory("HGET hot")]
    public async Task<string?> StackExchange_HGet_ServerRead() =>
        await _stackExchangeDb.HashGetAsync(HashKey, HashField);

    [Benchmark, BenchmarkCategory("HGET hot")]
    public ValueTask<string?> Respire_HGet_ServerRead() => _respire.Hashes.GetStringAsync(HashKey, HashField);

    [Benchmark, BenchmarkCategory("HGET hot")]
    public ValueTask<string?> Respire_HGet_ClientCacheHit() =>
        _respireCached.Hashes.GetStringAsync(HashKey, HashField);

    // EXISTS: cached scalar response path without string materialization.

    [Benchmark(Baseline = true), BenchmarkCategory("EXISTS hot")]
    public Task<bool> StackExchange_Exists_ServerRead() => _stackExchangeDb.KeyExistsAsync(ExistingKey);

    [Benchmark, BenchmarkCategory("EXISTS hot")]
    public ValueTask<bool> Respire_Exists_ServerRead() => _respire.ExistsAsync(ExistingKey);

    [Benchmark, BenchmarkCategory("EXISTS hot")]
    public ValueTask<bool> Respire_Exists_ClientCacheHit() => _respireCached.ExistsAsync(ExistingKey);
}
