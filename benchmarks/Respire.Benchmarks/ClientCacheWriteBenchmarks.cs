using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using DotNet.Testcontainers.Containers;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Diagnostics.Tracing;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Public cache-on writes with empty sharing, cache-off controls, and concurrent read/write churn.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[Config(typeof(MutationTraceConfig))]
public class ClientCacheWriteBenchmarks
{
    // Profile a separate execution so the bracketed timing samples exclude tracing overhead.
    // Override the runtime provider by name to retain contention, threading and GC events.
    public sealed class MutationTraceConfig : ManualConfig
    {
        public MutationTraceConfig() => AddDiagnoser(new EventPipeProfiler(EventPipeProfile.CpuSampling,
            [new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                (long)(ClrTraceEventParser.Keywords.Default | ClrTraceEventParser.Keywords.Contention
                    | ClrTraceEventParser.Keywords.Threading | ClrTraceEventParser.Keywords.GC))],
            performExtraBenchmarksRun: true));
    }

    private const int Callers = 50;
    private const int WritesPerCaller = 16;
    private readonly Task<long>[] _workers = new Task<long>[Callers];
    private readonly Task<string?>[] _reads = new Task<string?>[Callers];
    private readonly string[] _keys = Enumerable.Range(0, Callers).Select(index => $"cache:write:worker:{index}").ToArray();
    private readonly string _value = new('x', 128);
    private IContainer? _container;
    private RespireClient _coalescing = null!;
    private RespireClient _withoutCoalescing = null!;
    private RespireClient _withoutCache = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        try
        {
            var host = Environment.GetEnvironmentVariable("REDIS_HOST");
            var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort)
                ? configuredPort : 6379;
            if (string.IsNullOrEmpty(host))
            {
                _container = new RedisBuilder("redis:8.10").Build();
                await _container.StartAsync();
                host = _container.Hostname;
                port = _container.GetMappedPublicPort(6379);
            }
            var options = new RespireOptions
            {
                Endpoints = [new(host, port)], Connections = 1, Protocol = RespProtocol.Resp3,
                MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            };
            _withoutCache = await RespireClient.ConnectAsync(options);
            _withoutCoalescing = await RespireClient.ConnectAsync(options with { ClientSideCache = new() });
            _coalescing = await RespireClient.ConnectAsync(options with
            {
                ClientSideCache = new() { CoalesceConcurrentMisses = true },
            });
            if (await SetConcurrent50Coalescing() != Callers * WritesPerCaller
                || await SetConcurrent50WithoutCoalescing() != Callers * WritesPerCaller
                || await SetConcurrent50CacheDisabled() != Callers * WritesPerCaller
                || await SetConcurrent50SameKey() != Callers * WritesPerCaller
                || await FailedSetConcurrent50() != Callers * WritesPerCaller
                || await ReadWriteConcurrent50() != Callers * WritesPerCaller
                || await CoalescedGetMissBurst() != Callers * _value.Length)
                throw new InvalidOperationException("Cache write fixture did not preserve SET/GET results.");
            _coalescing.ClientSideCache!.Clear();
            _withoutCoalescing.ClientSideCache!.Clear();
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> SetConcurrent50Coalescing() => RunWrites(_coalescing, readAfterWrite: false);

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> SetConcurrent50WithoutCoalescing() => RunWrites(_withoutCoalescing, readAfterWrite: false);

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> SetConcurrent50CacheDisabled() => RunWrites(_withoutCache, readAfterWrite: false);

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> SetConcurrent50SameKey() => RunWrites(_coalescing, readAfterWrite: false, sameKey: true);

    /// <summary>Known-key server rejection retains conservative completion fencing.</summary>
    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public async Task<long> FailedSetConcurrent50()
    {
        for (var index = 0; index < _workers.Length; index++)
        {
            var key = _keys[index];
            _workers[index] = Task.Run(async () =>
            {
                long rejected = 0;
                for (var write = 0; write < WritesPerCaller; write++)
                {
                    try
                    {
                        using var reply = await _coalescing.ExecuteAsync(RespireCommands.String.SET,
                            new RespireValue[] { key, _value, "PX", 0 });
                        throw new InvalidOperationException("Redis accepted an invalid SET expiry.");
                    }
                    catch (RespireServerException error) when (error.Code == "ERR") { rejected++; }
                }
                return rejected;
            });
        }
        return (await Task.WhenAll(_workers)).Sum();
    }

    /// <summary>One SET/GET pair, with overlapping shared reads and local cache mutation fences.</summary>
    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> ReadWriteConcurrent50() => RunWrites(_coalescing, readAfterWrite: true);

    private async Task<long> RunWrites(RespireClient client, bool readAfterWrite, bool sameKey = false)
    {
        // These loops define fifty concurrent producers; each write or write/read pair is
        // counted once. BenchmarkDotNet still controls the iteration and invocation loops.
        for (var index = 0; index < _workers.Length; index++)
        {
            var key = _keys[sameKey ? 0 : index];
            _workers[index] = Task.Run(async () =>
            {
                long completed = 0;
                for (var write = 0; write < WritesPerCaller; write++)
                {
                    if (!await client.SetAsync(key, _value)) throw new InvalidOperationException("SET failed.");
                    if (readAfterWrite && await client.GetStringAsync(key) != _value)
                        throw new InvalidOperationException("GET returned an unexpected value.");
                    completed++;
                }
                return completed;
            });
        }
        return (await Task.WhenAll(_workers)).Sum();
    }

    /// <summary>One GET in a fifty-reader miss burst, including explicit eviction and result validation.</summary>
    [Benchmark(OperationsPerInvoke = Callers)]
    public async Task<long> CoalescedGetMissBurst()
    {
        _coalescing.ClientSideCache!.Clear();
        for (var index = 0; index < _reads.Length; index++)
            _reads[index] = _coalescing.GetStringAsync(_keys[0]).AsTask();
        var values = await Task.WhenAll(_reads);
        if (values.Any(value => value != _value)) throw new InvalidOperationException("Shared GET returned an unexpected value.");
        return values.Sum(value => (long)value!.Length);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_coalescing is not null) await _coalescing.DisposeAsync();
        if (_withoutCoalescing is not null) await _withoutCoalescing.DisposeAsync();
        if (_withoutCache is not null) await _withoutCache.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
