using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Respire.Benchmarks;

/// <summary>Paired stable primary RPCs against the same Redis server, with and without Sentinel routing.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SentinelReadyRoutingBenchmarks
{
    private readonly RespireKey _key = "sentinel-ready:read";
    private readonly string _payload = new('x', 32);
    private RespireClient _standalone = null!;
    private RespireClient _sentinel = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var redisPort = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredRedis) ? configuredRedis : 6379;
        var sentinelPort = int.TryParse(Environment.GetEnvironmentVariable("SENTINEL_PORT"), out var configuredSentinel) ? configuredSentinel : 26379;
        _standalone = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(host, redisPort)], Protocol = RespProtocol.Resp2, Connections = 1,
            ThreadPoolMonitoring = false,
        }).ConfigureAwait(false);
        _sentinel = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(host, sentinelPort)], SentinelPrimaryName = "mymaster",
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
        }).ConfigureAwait(false);
        if (!await _standalone.Strings.SetAsync(_key, _payload).ConfigureAwait(false))
            throw new InvalidOperationException("Failed to initialize the fixed benchmark payload.");

        // Finish startup monitor reconciliation before measuring a stable generation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var router = _sentinel.Core.Sentinel!;
        while (router.SubscribedSentinelCount < 1)
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        if (router.NotificationRediscovery is { } reconciliation)
            await reconciliation.WaitAsync(timeout.Token).ConfigureAwait(false);

        for (var index = 0; index < 50; index++)
        {
            await StandalonePing().ConfigureAwait(false);
            await SentinelPing().ConfigureAwait(false);
            if (await StandaloneString().ConfigureAwait(false) != _payload || await SentinelString().ConfigureAwait(false) != _payload
                || await StandaloneInteger().ConfigureAwait(false) != _payload.Length || await SentinelInteger().ConfigureAwait(false) != _payload.Length
                || (await StandaloneBytes().ConfigureAwait(false))?.Length != _payload.Length
                || (await SentinelBytes().ConfigureAwait(false))?.Length != _payload.Length)
                throw new InvalidOperationException("A stable routing benchmark returned an unexpected reply.");
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PING")]
    public ValueTask<TimeSpan> StandalonePing() => _standalone.PingAsync();
    [Benchmark, BenchmarkCategory("PING")]
    public ValueTask<TimeSpan> SentinelPing() => _sentinel.PingAsync();

    [Benchmark(Baseline = true), BenchmarkCategory("GET string")]
    public ValueTask<string?> StandaloneString() => _standalone.Strings.GetStringAsync(_key);
    [Benchmark, BenchmarkCategory("GET string")]
    public ValueTask<string?> SentinelString() => _sentinel.Strings.GetStringAsync(_key);

    // STRLEN returns a fixed integer; INCR would change response length across measurement phases.
    [Benchmark(Baseline = true), BenchmarkCategory("STRLEN")]
    public ValueTask<long> StandaloneInteger() => _standalone.Strings.LengthAsync(_key);
    [Benchmark, BenchmarkCategory("STRLEN")]
    public ValueTask<long> SentinelInteger() => _sentinel.Strings.LengthAsync(_key);

    [Benchmark(Baseline = true), BenchmarkCategory("GET bytes")]
    public ValueTask<byte[]?> StandaloneBytes() => _standalone.Strings.GetBytesAsync(_key);
    [Benchmark, BenchmarkCategory("GET bytes")]
    public ValueTask<byte[]?> SentinelBytes() => _sentinel.Strings.GetBytesAsync(_key);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        try { if (_sentinel is not null) await _sentinel.DisposeAsync().ConfigureAwait(false); }
        finally { if (_standalone is not null) await _standalone.DisposeAsync().ConfigureAwait(false); }
    }
}
