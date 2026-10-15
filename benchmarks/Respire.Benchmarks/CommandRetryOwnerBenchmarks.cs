using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

/// <summary>Healthy public dispatch, including a forced cache producer rather than a cache hit.</summary>
[MemoryDiagnoser]
public class CommandRetryOwnerBenchmarks
{
    private RespireClient _client = null!;
    private RespireFunction _function = null!;
    private readonly RespireKey _key = "retry:owners";
    private readonly RespireKey[] _keys = ["retry:owners"];
    private readonly string _value = "value";

    [Params("Standalone", "Sentinel", "Cluster", "Function", "CacheProducer", "ClusterCacheProducer")]
    public string Owner { get; set; } = "Standalone";

    [ParamsSource(nameof(Policies))]
    public string Policy { get; set; } = "Default";

    // The historical control predates RetryPolicy. Never silently substitute default
    // dispatch for an enabled policy when the selected API is unavailable.
    public IEnumerable<string> Policies => Environment.GetEnvironmentVariable("RESPIRE_BENCH_DEFAULT_ONLY") == "true"
        ? ["Default"] : ["Default", "Disabled", "Enabled"];

    [GlobalSetup]
    public async Task Setup()
    {
        ReportMemory("before-connect");
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = Owner switch
        {
            "Sentinel" => Port("SENTINEL_PORT", 26379),
            "Cluster" or "ClusterCacheProducer" => Port("CLUSTER_PORT", 6380),
            _ => Port("REDIS_PORT", 6379),
        };
        var options = new RespireOptions
        {
            Endpoints = [new(host, port)], Connections = 1, ThreadPoolMonitoring = false,
            Protocol = IsCacheProducer ? RespireProtocol.Resp3 : RespireProtocol.Resp2,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            SentinelPrimaryName = Owner == "Sentinel" ? "mymaster" : null,
            UseCluster = Owner is "Cluster" or "ClusterCacheProducer",
            ClusterTopologyRefreshInterval = null,
            ClientSideCache = IsCacheProducer ? new() : null,
        };
        SetPolicy(options);
        _client = await RespireClient.ConnectAsync(options).ConfigureAwait(false);
        await _client.SetAsync(_key, _value).ConfigureAwait(false);
        if (Owner == "Function")
        {
            var library = RespireFunctionLibrary.Create("""
                #!lua name=retry_owner_benchmark
                redis.register_function{function_name='retry_owner_length',
                    callback=function(keys,args) return redis.call('STRLEN',keys[1]) end,
                    flags={'no-writes'}}
                """, replace: true);
            await _client.Functions.LoadAsync(library).ConfigureAwait(false);
            _function = library.Function("retry_owner_length", readOnly: true);
            // Reflection stays outside measurement so the identical fixture compiles
            // against the pinned source before caller-declared retry categories existed.
            var declaration = typeof(RespireFunction).GetMethod("WithRetryCategory");
            if (declaration is not null)
            {
                var category = Enum.Parse(declaration.GetParameters()[0].ParameterType, "ReadOnly");
                _function = (RespireFunction)declaration.Invoke(_function, [category])!;
            }
            else if (Policy != "Default")
                throw new InvalidOperationException("The selected build cannot declare function retry risk.");
        }
        if (Owner == "Sentinel")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var router = _client.Core.Sentinel!;
            while (router.SubscribedSentinelCount < 1)
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
            if (router.NotificationRediscovery is { } reconciliation)
                await reconciliation.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        await VerifyRead().ConfigureAwait(false);
        ReportMemory("connected-idle");
        // Bounded sequential churn is outside the timed region.
        for (var i = 0; i < 256; i++)
            await VerifyRead().ConfigureAwait(false);
        ReportMemory("after-churn");
    }

    private void SetPolicy(RespireOptions options)
    {
        if (Policy == "Default") return;
        if (Policy is not ("Disabled" or "Enabled"))
            throw new InvalidOperationException("Unknown retry policy mode.");
        var property = typeof(RespireOptions).GetProperty("RetryPolicy")
            ?? throw new InvalidOperationException("The selected build has no command retry policy.");
        var policy = Activator.CreateInstance(property.PropertyType)!;
        if (Policy == "Disabled")
            property.PropertyType.GetProperty("MaxAttempts")!.SetValue(policy, 1);
        property.SetValue(options, policy);
    }

    [Benchmark]
    public ValueTask<long> Read()
    {
        if (Owner == "Function") return _client.Functions.ExecuteIntegerAsync(_function, _keys);
        if (IsCacheProducer) return ClearAndRead();
        return _client.Strings.LengthAsync(_key);
    }

    private async ValueTask<long> ClearAndRead()
    {
        // Clearing the single local entry is measured equally in every policy mode.
        // Every invocation dispatches a producer; no invocation measures a local hit.
        _client.ClientSideCache!.Clear();
        return (await _client.GetStringAsync(_key).ConfigureAwait(false))?.Length ?? -1;
    }

    private bool IsCacheProducer => Owner is "CacheProducer" or "ClusterCacheProducer";

    private async Task VerifyRead()
    {
        var misses = _client.ClientSideCache?.GetStatistics().Misses;
        if (await Read().ConfigureAwait(false) != _value.Length)
            throw new InvalidOperationException("Retry owner fixture returned an unexpected result.");
        if (misses is { } before && (_client.ClientSideCache!.Count != 1
            || _client.ClientSideCache.GetStatistics().Misses != before + 1))
            throw new InvalidOperationException("The cache case must dispatch one producer and retain its result.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _client = null!;
        _function = null!;
        ReportMemory("after-dispose");
    }

    private static int Port(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var port) ? port : fallback;

    private void ReportMemory(string stage)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("RECEIVE_MEMORY " + JsonSerializer.Serialize(new
        {
            stage, owner = Owner, policy = Policy, managedBytes = GC.GetTotalMemory(false),
            pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes,
            workingSetBytes = process.WorkingSet64,
        }));
    }
}
