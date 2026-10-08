using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Respire.Commands;
using Respire.Protocol;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
public class ClientCacheClassificationBenchmarks
{
    private const int Callers = 50;
    private const int WritesPerCaller = 16;
    private readonly Task<long>[] _workers = new Task<long>[Callers];
    private readonly string[] _keys = Enumerable.Range(0, Callers).Select(index => $"cache:classification:{index}").ToArray();
    private readonly string _value = new('x', 128);
    private RespireValue[][] _setArguments = null!;
    private RespireValue[][] _multiSetArguments = null!;
    private RespireValue[][] _deleteArguments = null!;
    private IContainer? _container;
    private RespireClient _cached = null!;
    private RespireClient _coalescing = null!;
    private RespireClient _uncached = null!;
    private readonly SetCommand _classificationSet = new("key", "value", default, SetWhen.Always, false);
    private readonly CmdN _classificationMulti = new(Verbs.Del, ["first", "second"]);
    private readonly CatalogCommand _classificationRaw = new(RespireCommands.String.SET, ["key", "value"]);

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
            _uncached = await RespireClient.ConnectAsync(options);
            _cached = await RespireClient.ConnectAsync(options with { ClientSideCache = new() });
            _coalescing = await RespireClient.ConnectAsync(options with
            {
                ClientSideCache = new() { CoalesceConcurrentMisses = true },
            });
            _setArguments = _keys.Select(key => new RespireValue[] { key, _value }).ToArray();
            _multiSetArguments = _keys.Select(key => new RespireValue[] { key, _value, key + ":second", _value }).ToArray();
            _deleteArguments = _keys.Select(key => new RespireValue[] { key, key + ":second" }).ToArray();
            if (ClassifyTypedSet() != (int)RespireCacheMutation.Mutation
                || ClassifyMultiKeyMutation() != (int)RespireCacheMutation.Mutation
                || ClassifyRawCatalogSet() != (int)RespireCacheMutation.Mutation
                || await TypedSetConcurrent50Cache() != Callers * WritesPerCaller
                || await TypedSetConcurrent50Coalescing() != Callers * WritesPerCaller
                || await TypedSetConcurrent50CacheDisabled() != Callers * WritesPerCaller
                || await RawSetConcurrent50Cache() != Callers * WritesPerCaller
                || await RawSetConcurrent50CacheDisabled() != Callers * WritesPerCaller
                || await MultiKeyMutationConcurrent50Cache() != Callers * WritesPerCaller)
                throw new InvalidOperationException("Cache classification fixture produced unexpected results.");
            // The multi-key control removes its keys. Restore the read control's source afterward.
            if (!await _cached.SetAsync(_keys[0], _value) || await GetMissCache() != _value)
                throw new InvalidOperationException("Cache classification GET fixture was not primed.");
            _cached.ClientSideCache!.Clear();
            _coalescing.ClientSideCache!.Clear();
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> TypedSetConcurrent50Cache() => RunWrites(_cached, WriteKind.TypedSet);

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> TypedSetConcurrent50Coalescing() => RunWrites(_coalescing, WriteKind.TypedSet);

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> TypedSetConcurrent50CacheDisabled() => RunWrites(_uncached, WriteKind.TypedSet);

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> RawSetConcurrent50Cache() => RunWrites(_cached, WriteKind.RawSet);

    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> RawSetConcurrent50CacheDisabled() => RunWrites(_uncached, WriteKind.RawSet);

    /// <summary>One two-key MSET/DEL pair, including validation and both mutation fences.</summary>
    [Benchmark(OperationsPerInvoke = Callers * WritesPerCaller)]
    public Task<long> MultiKeyMutationConcurrent50Cache() => RunWrites(_cached, WriteKind.MultiKeyPair);

    [Benchmark]
    public int ClassifyTypedSet() => Classify(in _classificationSet, "SET");

    [Benchmark]
    public int ClassifyMultiKeyMutation() => Classify(in _classificationMulti, "DEL");

    [Benchmark]
    public int ClassifyRawCatalogSet() => Classify(in _classificationRaw, "SET");

    // Use the existing constrained interface API, so the identical fixture builds on both versions.
    // Preserve the measured call boundary when classification becomes cheaper than BDN's empty
    // method overhead. Both revisions include this same boundary; it is not a production attribute.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Classify<TCommand>(in TCommand command, string operation) where TCommand : struct, IRespCommand
        => (int)command.GetCacheMutation(operation);

    /// <summary>One default independent GET miss, including eviction and result validation.</summary>
    [Benchmark]
    public async Task<string?> GetMissCache()
    {
        _cached.ClientSideCache!.Clear();
        var value = await _cached.GetStringAsync(_keys[0]);
        if (value != _value) throw new InvalidOperationException("GET returned an unexpected value.");
        return value;
    }

    private enum WriteKind { TypedSet, RawSet, MultiKeyPair }

    private async Task<long> RunWrites(RespireClient client, WriteKind kind)
    {
        // Fifty concurrent producers define the workload. BDN controls invocation/iteration counts.
        for (var index = 0; index < _workers.Length; index++)
        {
            var worker = index;
            _workers[index] = Task.Run(async () =>
            {
                for (var write = 0; write < WritesPerCaller; write++)
                {
                    if (kind == WriteKind.TypedSet)
                    {
                        if (!await client.SetAsync(_keys[worker], _value)) throw new InvalidOperationException("SET failed.");
                        continue;
                    }
                    using var result = await client.ExecuteAsync(
                        kind == WriteKind.RawSet ? RespireCommands.String.SET : RespireCommands.String.MSET,
                        kind == WriteKind.RawSet ? _setArguments[worker] : _multiSetArguments[worker]);
                    if (result.AsString() != "OK") throw new InvalidOperationException("Raw SET/MSET failed.");
                    if (kind == WriteKind.MultiKeyPair)
                    {
                        using var removed = await client.ExecuteAsync(RespireCommands.Key.DEL, _deleteArguments[worker]);
                        if (removed.AsInteger() != 2) throw new InvalidOperationException("DEL did not remove both keys.");
                    }
                }
                return (long)WritesPerCaller;
            });
        }
        return (await Task.WhenAll(_workers)).Sum();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_cached is not null) await _cached.DisposeAsync();
        if (_coalescing is not null) await _coalescing.DisposeAsync();
        if (_uncached is not null) await _uncached.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
