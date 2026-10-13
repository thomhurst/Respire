#if STACKEXCHANGE_REDIS_V4
using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using StackExchange.Redis.Caching;
using Testcontainers.Redis;

namespace Respire.ComparisonBenchmarks;

/// <summary>Like-for-like string GET cache hits using the 4.0 ValueTask API.</summary>
[MemoryDiagnoser]
public class ClientCacheComparisonBenchmarks
{
    private IContainer? _redisContainer;
    private RespireClient _respire = null!;
    private ConnectionMultiplexer _stackExchange = null!;
    private ConnectionMultiplexer _control = null!;
    private IDatabase _cachedDb = null!;
    private IDatabase _controlDb = null!;
    private readonly string _prefix = $"respire:benchmark:cache-comparison:{Guid.NewGuid():N}:";
    private string _key = null!;
    private readonly string _value = "Hello, client cache!";

    [Params(false, true)]
    public bool Missing { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort)
            ? configuredPort : 6379;
        if (string.IsNullOrEmpty(host))
        {
            _redisContainer = new RedisBuilder("redis:8.10").Build();
            await _redisContainer.StartAsync();
            host = "localhost";
            port = _redisContainer.GetMappedPublicPort(6379);
        }

        var connectionString = $"{host}:{port}";
        _key = _prefix + "key";
        var controlOptions = ConfigurationOptions.Parse(connectionString);
        controlOptions.AllowAdmin = true; // INFO commandstats is a setup-only validation probe.
        _control = await ConnectionMultiplexer.ConnectAsync(controlOptions);
        _controlDb = _control.GetDatabase();
        if (!Missing)
        {
            await _controlDb.StringSetAsync(_key, _value);
        }

        // Both caches use bounded storage, RESP3 broadcast tracking and a long local
        // lifetime so this measures steady hits rather than periodic expiration.
        _respire = await RespireClient.ConnectAsync(RespireOptions.Parse(connectionString) with
        {
            ClientSideCache = new()
            {
                TrackingMode = RespireClientTrackingMode.Broadcast,
                KeyPrefixes = [_prefix],
                LocalExpiration = TimeSpan.FromDays(1),
            },
        });
        var options = ConfigurationOptions.Parse(connectionString);
        options.Protocol = RedisProtocol.Resp3;
        options.ClientCache = new CacheOptions
        {
            Prefixes = [_prefix],
            MaxBytes = 64L * 1024 * 1024,
            MaxEntries = 10_000,
            DefaultPolicy = new CachePolicy { TimeToLive = TimeSpan.FromDays(1) },
        };
        _stackExchange = await ConnectionMultiplexer.ConnectAsync(options);
        _cachedDb = _stackExchange.GetDatabase();

        // The 4.0 default admission policy stores on the second miss.
        for (var i = 0; i < 3; i++)
        {
            await StackExchange_Get_ClientCacheHit();
            await Respire_Get_ClientCacheHit();
        }

        // Positive control: the counter must detect an uncached GET. Use an isolated
        // Redis instance; unrelated GET traffic would intentionally fail this check.
        var beforeControl = await GetServerGetCount();
        await _controlDb.StringGetAsync(_key);
        var beforeHits = await GetServerGetCount();
        if (beforeHits != beforeControl + 1)
        {
            throw new InvalidOperationException("GET counter control failed; use an isolated Redis instance.");
        }
        for (var i = 0; i < 8; i++)
        {
            var expected = Missing ? null : _value;
            if (await StackExchange_Get_ClientCacheHit() != expected ||
                await Respire_Get_ClientCacheHit() != expected)
            {
                throw new InvalidOperationException("Client-cache benchmark returned an unexpected value.");
            }
        }
        if (await GetServerGetCount() != beforeHits)
        {
            throw new InvalidOperationException("Client-cache benchmark reads reached Redis after priming.");
        }
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<string?> StackExchange_Get_ClientCacheHit() =>
        (string?)await _cachedDb.Strings.GetAsync(_key);

    [Benchmark]
    public ValueTask<string?> Respire_Get_ClientCacheHit() => _respire.GetStringAsync(_key);

    private async Task<long> GetServerGetCount()
    {
        var info = (string?)await _controlDb.ExecuteAsync("INFO", "commandstats")
            ?? throw new InvalidOperationException("Redis did not return command statistics.");
        var line = info.Split('\n').SingleOrDefault(static line => line.StartsWith("cmdstat_get:", StringComparison.Ordinal));
        if (line is null)
        {
            return 0;
        }
        var calls = line["cmdstat_get:".Length..].Split(',').Single(static field => field.StartsWith("calls=", StringComparison.Ordinal));
        return long.Parse(calls["calls=".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _controlDb.KeyDeleteAsync(_key);
        await _respire.DisposeAsync();
        await _stackExchange.DisposeAsync();
        await _control.DisposeAsync();
        if (_redisContainer is not null)
        {
            await _redisContainer.DisposeAsync();
        }
    }
}
#endif
