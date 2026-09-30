#define RESPIRE_CACHE_ASIDE_API
using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;

namespace Respire.Benchmarks;

/// <summary>Compares cache-aside calls with equivalent GET/SET NX GET composition on the baseline.</summary>
[MemoryDiagnoser]
[OperationsPerSecond]
public class CacheAsideBenchmarks
{
    public enum Workload { DefaultSingle, CoalescedSingle, CoalescedBurst }
    [ParamsAllValues] public Workload Scenario { get; set; }
    private delegate ValueTask<string?> Read(RespireClient client, RespireKey key,
        Func<CancellationToken, ValueTask<string?>> factory, TimeSpan ttl, CancellationToken cancellationToken);

    private RespireClient _client = null!;
    private Read _read = null!;
    private Func<CancellationToken, ValueTask<string?>> _factory = null!;
    private readonly RespireKey _key = "benchmark:cache-aside";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private long _factoryCalls;
    private long _operations;
    private long _missOperations;
    private long _started;
    private TimeSpan _cpuStarted;

    [GlobalSetup]
    public async Task Setup()
    {
        var options = RespireOptions.Parse($"{Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1"}:{Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"}") with
        {
            Connections = 1,
            ClientSideCache = new() { CoalesceConcurrentMisses = Scenario != Workload.DefaultSingle },
        };
        _client = await RespireClient.ConnectAsync(options);
        // CI removes only the file-local symbol for the old-build fixture. Both adapters
        // use the same delegate signature; binding is checked by the compiler, not reflection.
#if RESPIRE_CACHE_ASIDE_API
        _read = static (client, key, factory, ttl, token) => client.GetOrSetAsync(key, factory, ttl, token);
#else
        _read = ReferenceReadAsync;
#endif
        _factory = FactoryAsync;
        await _client.SetAsync(_key, "value", TimeSpan.FromHours(1));
        _ = await _client.GetStringAsync(_key);
        if (await _read(_client, _key, _factory, Ttl, default) != "value" || _factoryCalls != 0)
            throw new InvalidOperationException("The selected cache-aside adapter did not return the primed hit.");
        _cpuStarted = Process.GetCurrentProcess().TotalProcessorTime;
        _started = Stopwatch.GetTimestamp();
    }

    [Benchmark]
    public ValueTask<string?> HotGetOrSet()
    {
        Interlocked.Increment(ref _operations);
        return _read(_client, _key, _factory, Ttl, default);
    }

    [Benchmark]
    public async Task<string?[]> MissBurst()
    {
        Interlocked.Increment(ref _operations);
        _missOperations++;
        // Both phases include the same eviction and complete burst. The factory's 1 ms
        // asynchronous work represents a loader, rather than raw Redis command throughput.
        await _client.DeleteAsync(_key);
        var reads = new Task<string?>[Scenario == Workload.CoalescedBurst ? 32 : 1];
        for (var index = 0; index < reads.Length; index++)
            reads[index] = _read(_client, _key, _factory, Ttl, default).AsTask();
        return await Task.WhenAll(reads);
    }

    private async ValueTask<string?> FactoryAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _factoryCalls);
        await Task.Delay(1, token);
        return "value";
    }

    private static async ValueTask<string?> ReferenceReadAsync(RespireClient client, RespireKey key,
        Func<CancellationToken, ValueTask<string?>> factory, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var existing = await client.GetStringAsync(key, cancellationToken);
        if (existing is not null) return existing;
        var created = await factory(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (created is null) return null;
        return await client.Strings.GetAndSetAsync(key, created, ttl, SetWhen.NotExists, cancellationToken) ?? created;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        Console.WriteLine("CACHE_ASIDE_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            operation = _missOperations == 0 ? nameof(HotGetOrSet) : nameof(MissBurst),
            scenario = Scenario.ToString(), operations = _operations, factoryCalls = _factoryCalls,
            elapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
            cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - _cpuStarted).TotalMilliseconds,
        }));
        await _client.DisposeAsync();
    }
}
