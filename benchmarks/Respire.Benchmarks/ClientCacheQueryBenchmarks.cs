using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Fifty SET producers beside fifty query producers, with affected-key and no-write controls.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ClientCacheQueryBenchmarks
{
    private const int Callers = 50;
    private const int Rounds = 16;
    private static readonly Func<long>? s_lockContentions = typeof(object).Assembly.GetType("System.Threading.Lock")
        ?.GetProperty("ContentionCount", BindingFlags.Static | BindingFlags.NonPublic)?.GetMethod?.CreateDelegate<Func<long>>();
    private readonly Task<long>[] _workers = new Task<long>[2 * Callers];
    private readonly string[] _writeKeys = Enumerable.Range(0, Callers).Select(index => $"cache:query:write:{index}").ToArray();
    private readonly string[] _readKeys = Enumerable.Range(0, Callers * Rounds).Select(index => $"cache:query:read:{index}").ToArray();
    private readonly string _value = new('x', 128);
    private IContainer? _container;
    private RespireClient _sharing = null!;
    private RespireClient _independent = null!;
    private RespireClient _cacheOff = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        if (s_lockContentions is null) throw new NotSupportedException("Query contention diagnostics require the pinned net10 runtime lock counter.");
        ReportMemory("before-connect");
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
            _cacheOff = await RespireClient.ConnectAsync(options);
            _independent = await RespireClient.ConnectAsync(options with { ClientSideCache = new() });
            _sharing = await RespireClient.ConnectAsync(options with
            {
                ClientSideCache = new() { CoalesceConcurrentMisses = true },
            });
            foreach (var key in _readKeys.Concat(_writeKeys))
                if (!await _cacheOff.SetAsync(key, _value)) throw new InvalidOperationException("Seed SET failed.");
            ReportMemory("connected-idle");
            if (await SetConcurrent50() != Callers * Rounds
                || await SetConcurrent50CacheOff() != Callers * Rounds)
                throw new InvalidOperationException("SET fixture validation failed.");
            await ValidateQueries("unrelated-sharing", _sharing, writes: true, affected: false);
            await ValidateQueries("unrelated-independent", _independent, writes: true, affected: false);
            await ValidateQueries("affected-sharing", _sharing, writes: true, affected: true);
            await ValidateQueries("no-writes", _sharing, writes: false, affected: false);
            // Historical key cardinality must not retain pending generations. These
            // temporary missing-key strings and replies have no fixture-owned roots.
            for (var index = 0; index < 1_500; index++)
                if (await _sharing.Strings.LengthAsync("cache:query:churn:" + index) != 0)
                    throw new InvalidOperationException("Churn key unexpectedly exists.");
            _sharing.ClientSideCache!.Clear();
            _independent.ClientSideCache!.Clear();
            ReportMemory("after-churn");
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    [Benchmark(OperationsPerInvoke = Callers * Rounds)]
    public Task<long> SetConcurrent50() => Run(_sharing, queries: false, writes: true, affected: false);

    [Benchmark(OperationsPerInvoke = Callers * Rounds)]
    public Task<long> SetConcurrent50CacheOff() => Run(_cacheOff, queries: false, writes: true, affected: false);

    // Each mixed unit is one SET and one cold-query/first-reuse pair. The method
    // includes Clear, producer scheduling, tracking, result validation, and local hits.
    [Benchmark(OperationsPerInvoke = Callers * Rounds)]
    public Task<long> UnrelatedQueriesWithWrites() => Run(_sharing, queries: true, writes: true, affected: false);

    [Benchmark(OperationsPerInvoke = Callers * Rounds)]
    public Task<long> UnrelatedQueriesWithoutCoalescing() => Run(_independent, queries: true, writes: true, affected: false);

    [Benchmark(OperationsPerInvoke = Callers * Rounds)]
    public Task<long> AffectedQueriesWithWrites() => Run(_sharing, queries: true, writes: true, affected: true);

    // One cold query plus first reuse, without SET traffic.
    [Benchmark(OperationsPerInvoke = Callers * Rounds)]
    public Task<long> QueryMissesWithoutWrites() => Run(_sharing, queries: true, writes: false, affected: false);

    private async Task<long> Run(RespireClient client, bool queries, bool writes, bool affected)
    {
        if (queries) client.ClientSideCache!.Clear();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var index = 0; index < Callers; index++)
        {
            var worker = index;
            _workers[index] = writes ? Task.Run(async () =>
            {
                await start.Task;
                for (var round = 0; round < Rounds; round++)
                    if (!await client.SetAsync(_writeKeys[worker], _value)) throw new InvalidOperationException("SET failed.");
                return (long)Rounds;
            }) : Task.FromResult(0L);
            _workers[Callers + index] = queries ? Task.Run(async () =>
            {
                await start.Task;
                long length = 0;
                for (var round = 0; round < Rounds; round++)
                {
                    var key = affected ? _writeKeys[worker] : _readKeys[worker * Rounds + round];
                    var first = await client.Strings.LengthAsync(key);
                    var reused = await client.Strings.LengthAsync(key);
                    if (first != _value.Length || reused != _value.Length)
                        throw new InvalidOperationException("STRLEN returned an unexpected value.");
                    length += first + reused;
                }
                return length;
            }) : Task.FromResult(0L);
        }
        start.SetResult();
        return (await Task.WhenAll(_workers)).Sum();
    }

    private async Task ValidateQueries(string workload, RespireClient client, bool writes, bool affected)
    {
        var before = client.ClientSideCache!.GetStatistics();
        var beforeContentions = s_lockContentions!();
        if (await Run(client, queries: true, writes, affected) != Callers * Rounds * (2 * _value.Length + (writes ? 1 : 0)))
            throw new InvalidOperationException("Mixed fixture validation failed.");
        var lockContentions = s_lockContentions() - beforeContentions;
        var after = client.ClientSideCache.GetStatistics();
        Console.WriteLine("QUERY_VALIDATION " + JsonSerializer.Serialize(new
        {
            workload, queryCalls = 2 * Callers * Rounds, writes = writes ? Callers * Rounds : 0,
            hits = after.Hits - before.Hits, misses = after.Misses - before.Misses,
            residentEntries = after.Count, managedLockContentions = lockContentions,
        }));
    }

    private void ReportMemory(string stage)
    {
        // Reflection is outside measured methods, after every producer has joined.
        // The same copied fixture supports baselines without the new pending index.
        var pending = PendingDependencies(_sharing) + PendingDependencies(_independent);
        if (pending != 0) throw new InvalidOperationException("Completed workload retained pending dependency keys.");
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("RECEIVE_MEMORY " + JsonSerializer.Serialize(new
        {
            stage, pendingDependencyKeys = pending, churnKeys = 1_500,
            cacheEntries = (_sharing?.ClientSideCache?.Count ?? 0) + (_independent?.ClientSideCache?.Count ?? 0),
            managedBytes = GC.GetTotalMemory(false), pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes, workingSetBytes = process.WorkingSet64,
        }));
    }

    private static int PendingDependencies(RespireClient? client)
    {
        var cache = client?.ClientSideCache;
        return cache?.GetType().GetField("_queryDependencies", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(cache) is ICollection dependencies ? dependencies.Count : 0;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_sharing is not null) { await _sharing.DisposeAsync(); _sharing = null!; }
        if (_independent is not null) { await _independent.DisposeAsync(); _independent = null!; }
        if (_cacheOff is not null) { await _cacheOff.DisposeAsync(); _cacheOff = null!; }
        if (_container is not null) { await _container.DisposeAsync(); _container = null; }
        ReportMemory("after-dispose");
    }
}
