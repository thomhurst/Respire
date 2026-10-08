using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
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
        // Enable runtime contention events only for this untimed fixture validation.
        // Match the actual query gate's native wait handle, not whole-process contention totals.
        using var queryContentions = await QueryGateContentionProbe.CreateAsync(client.ClientSideCache!);
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
            queryGateContentionsObserved = queryContentions.Count,
            queryGateCompletedWaitsObserved = queryContentions.CompletedWaits,
            queryGateWaitMillisecondsObserved = queryContentions.WaitMilliseconds,
            queryGateMaximumWaitMillisecondsObserved = queryContentions.MaximumWaitMilliseconds,
            queryGateOutstandingWaitsObserved = queryContentions.OutstandingWaits,
            queryGatePositiveControl = true,
        }));
    }

    private sealed class QueryGateContentionProbe : EventListener
    {
        private readonly ConcurrentQueue<nint> _warmLockIds = new();
        private readonly ConcurrentDictionary<long, byte> _waitingThreads = new();
        private int _ready;
        private nint _target;
        private long _count;
        private long _completedWaits;
        private double _waitNanoseconds;
        private double _maximumWaitNanoseconds;
        internal long Count => Interlocked.Read(ref _count);
        internal long CompletedWaits => Interlocked.Read(ref _completedWaits);
        internal int OutstandingWaits => _waitingThreads.Count;
        internal double WaitMilliseconds => Volatile.Read(ref _waitNanoseconds) / 1_000_000;
        internal double MaximumWaitMilliseconds => Volatile.Read(ref _maximumWaitNanoseconds) / 1_000_000;

        private QueryGateContentionProbe()
        {
            // EventListener discovers sources in its base constructor, before these fields exist.
            Volatile.Write(ref _ready, 1);
            foreach (var source in EventSource.GetSources()) OnEventSourceCreated(source);
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (Volatile.Read(ref _ready) != 0 && source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Informational, (EventKeywords)0x4000);
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventSource.Name != "Microsoft-Windows-DotNETRuntime" || args.Payload is not { Count: >= 3 }) return;
            if (args.EventId == 91 && _waitingThreads.TryRemove(args.OSThreadId, out _))
            {
                var duration = Convert.ToDouble(args.Payload[2], System.Globalization.CultureInfo.InvariantCulture);
                AddWait(duration);
                return;
            }
            if (args.EventId != 81) return;
            var lockId = args.Payload[2] switch
            {
                nint pointer => pointer,
                long value => (nint)value,
                ulong value => (nint)unchecked((long)value),
                _ => 0,
            };
            if (lockId == 0) return;
            var target = Volatile.Read(ref _target);
            if (target == 0) _warmLockIds.Enqueue(lockId);
            else if (lockId == target)
            {
                _waitingThreads.TryAdd(args.OSThreadId, 0);
                Interlocked.Increment(ref _count);
            }
        }

        private void AddWait(double duration)
        {
            var total = Volatile.Read(ref _waitNanoseconds);
            while (Interlocked.CompareExchange(ref _waitNanoseconds, total + duration, total) != total)
                total = Volatile.Read(ref _waitNanoseconds);
            var maximum = Volatile.Read(ref _maximumWaitNanoseconds);
            while (duration > maximum && Interlocked.CompareExchange(ref _maximumWaitNanoseconds, duration, maximum) != maximum)
                maximum = Volatile.Read(ref _maximumWaitNanoseconds);
            Interlocked.Increment(ref _completedWaits);
        }

        internal static async Task<QueryGateContentionProbe> CreateAsync(object cache)
        {
            var gate = cache.GetType().GetField("_queryLock", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(cache)
                ?? throw new InvalidOperationException("Query publication gate is missing.");
            var enter = gate.GetType().GetMethod("Enter") ?? throw new NotSupportedException("Query diagnostics require net10 Lock.Enter.");
            var exit = gate.GetType().GetMethod("Exit") ?? throw new NotSupportedException("Query diagnostics require net10 Lock.Exit.");
            var lockId = gate.GetType().GetProperty("LockIdForEvents", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new NotSupportedException("Pinned runtime lock identity is missing.");
            var listener = new QueryGateContentionProbe();
            try
            {
                await listener.CalibrateAsync(gate, enter, exit, lockId);
                return listener;
            }
            catch { listener.Dispose(); throw; }
        }

        private async Task CalibrateAsync(object gate, MethodInfo enter, MethodInfo exit, PropertyInfo lockId)
        {
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var waiterStarted = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holding = Task.Factory.StartNew(() =>
            {
                enter.Invoke(gate, null);
                try
                {
                    held.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Query gate positive control was not released.");
                }
                finally { exit.Invoke(gate, null); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task? waiting = null;
            try
            {
                if (!held.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Query gate holder did not start.");
                waiting = Task.Factory.StartNew(() =>
                {
                    waiterStarted.SetResult(Thread.CurrentThread);
                    enter.Invoke(gate, null);
                    exit.Invoke(gate, null);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var thread = await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (!SpinWait.SpinUntil(() => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Query gate positive control did not park.");
                var identity = (nint)lockId.GetValue(gate)!;
                if (!SpinWait.SpinUntil(() => _warmLockIds.Contains(identity), TimeSpan.FromSeconds(10)))
                    throw new InvalidOperationException("Runtime contention events did not identify the controlled query gate.");
                Volatile.Write(ref _target, identity);
                while (_warmLockIds.TryDequeue(out _)) { }
            }
            finally
            {
                release.Set();
                await Task.WhenAll(waiting is null ? [holding] : [holding, waiting]).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private void ReportMemory(string stage)
    {
        // Reflection is outside measured methods, after every producer has joined.
        // The same copied fixture supports baselines without the new pending index.
        var pending = PendingDependencies(_sharing?.ClientSideCache) + PendingDependencies(_independent?.ClientSideCache);
        if (pending != 0) throw new InvalidOperationException("Completed workload retained pending dependency keys.");
        var sharingIdle = IdleQueryStorage(_sharing?.ClientSideCache);
        var independentIdle = IdleQueryStorage(_independent?.ClientSideCache);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("RECEIVE_MEMORY " + JsonSerializer.Serialize(new
        {
            stage, pendingDependencyKeys = pending, churnKeys = 1_500,
            idleQueryLeases = sharingIdle.Leases + independentIdle.Leases,
            idleQueryStates = sharingIdle.States + independentIdle.States,
            retainedIdleDependencies = sharingIdle.RetainedDependencies + independentIdle.RetainedDependencies,
            cacheEntries = (_sharing?.ClientSideCache?.Count ?? 0) + (_independent?.ClientSideCache?.Count ?? 0),
            managedBytes = GC.GetTotalMemory(false), pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes, workingSetBytes = process.WorkingSet64,
        }));
    }

    private static (int Leases, int States, int RetainedDependencies) IdleQueryStorage(object? cache)
    {
        if (cache is null) return default;
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = cache.GetType();
        var leaseField = type.GetField("_idleQueryLeases", members);
        var stateField = type.GetField("_idleQueryStates", members);
        if (leaseField is null && stateField is null)
        {
            // The same copied fixture runs against the pre-pooling baseline.
            if (type.GetNestedType("TestInspection", BindingFlags.NonPublic)?.GetProperty("IdleQueryStorage", members) is not null)
                throw new InvalidOperationException("Idle storage diagnostic exists but pooled storage is missing.");
            return default;
        }
        if (leaseField is null || stateField is null) throw new InvalidOperationException("Incomplete query storage diagnostics.");
        var leases = 0;
        var states = 0;
        var retained = 0;
        for (var state = stateField.GetValue(cache); state is not null; state = state.GetType().GetField("Next", members)!.GetValue(state))
        {
            if (++states > 256) throw new InvalidOperationException("Query state pool exceeds its per-client bound.");
            if (state.GetType().GetField("Key", members)!.GetValue(state) is not RespireKey key || !key.Equals(default(RespireKey))) retained++;
        }
        for (var lease = leaseField.GetValue(cache); lease is not null; lease = lease.GetType().GetField("Next", members)!.GetValue(lease))
        {
            if (++leases > 64) throw new InvalidOperationException("Query lease pool exceeds its per-client bound.");
            var leaseType = lease.GetType();
            var capacity = (int)leaseType.GetProperty("Capacity", members)!.GetValue(lease)!;
            if (capacity > 16) throw new InvalidOperationException("Large stamp storage was retained.");
            var dependency = leaseType.GetMethod("GetDependency", members)!;
            for (var index = 0; index < capacity; index++)
            {
                var stamp = dependency.Invoke(lease, [index])!;
                // The positional record exposes its State property publicly.
                if (stamp.GetType().GetProperty("State")!.GetValue(stamp) is not null) retained++;
            }
        }
        if (retained != 0) throw new InvalidOperationException("Idle query storage retained dependency keys or states.");
        return (leases, states, retained);
    }

    private static int PendingDependencies(object? cache)
    {
        if (cache is null) return 0;
        var type = cache.GetType();
        var field = type.GetField("_queryDependencies", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            // The pre-fencing baseline lacks both the index and its inspection diagnostic.
            // A renamed index in a fencing implementation must fail rather than report zero.
            var inspection = type.GetNestedType("TestInspection", BindingFlags.NonPublic);
            if (inspection?.GetProperty("PendingQueryDependencyCount", BindingFlags.Instance | BindingFlags.NonPublic) is not null)
                throw new InvalidOperationException("Query dependency diagnostic exists but its pending index is missing.");
            return 0;
        }
        if (field.GetValue(cache) is not ICollection dependencies)
            throw new InvalidOperationException("Pending query dependency index is not a collection.");
        return dependencies.Count;
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
