using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

/// <summary>Healthy public batch and unwatched transaction dispatch with default, disabled and enabled retry ownership.</summary>
/// <remarks>
/// Both units contain two read-only members on one hash slot, so the enabled policy owns a
/// whole-unit retry on every topology without changing what the healthy path sends.
/// </remarks>
[MemoryDiagnoser]
public class UnitRetryBenchmarks
{
    private RespireClient _client = null!;
    private string _method = null!;
    private readonly RespireKey _first = "{retry-units}:first";
    private readonly RespireKey _second = "{retry-units}:second";

    [Params("Standalone", "Sentinel", "Cluster")]
    public string Owner { get; set; } = "Standalone";

    [ParamsSource(nameof(Policies))]
    public string Policy { get; set; } = "Default";

    // Historical controls predate whole-unit retries. They measure default dispatch only,
    // because an enabled policy there would not own batch or transaction retries.
    public IEnumerable<string> Policies => Environment.GetEnvironmentVariable("RESPIRE_BENCH_DEFAULT_ONLY") == "true"
        ? ["Default"] : ["Default", "Disabled", "Enabled"];

    [GlobalSetup(Target = nameof(Batch))]
    public Task SetupBatch() => Setup(nameof(Batch));

    [GlobalSetup(Target = nameof(Transaction))]
    public Task SetupTransaction() => Setup(nameof(Transaction));

    private async Task Setup(string method)
    {
        _method = method;
        ReportMemory("before-connect");
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = Owner switch
        {
            "Sentinel" => Port("SENTINEL_PORT", 26379),
            "Cluster" => Port("CLUSTER_PORT", 6380),
            _ => Port("REDIS_PORT", 6379),
        };
        var options = new RespireOptions
        {
            Endpoints = [new(host, port)], Connections = 1, ThreadPoolMonitoring = false,
            Protocol = RespProtocol.Resp2,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            SentinelPrimaryName = Owner == "Sentinel" ? "mymaster" : null,
            UseCluster = Owner == "Cluster",
            ClusterTopologyRefreshInterval = null,
        };
        SetPolicy(options);
        _client = await RespireClient.ConnectAsync(options).ConfigureAwait(false);
        await _client.SetAsync(_first, "value").ConfigureAwait(false);
        await _client.Keys.DeleteAsync(_second).ConfigureAwait(false);
        if (Owner == "Sentinel")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var router = _client.Core.Sentinel!;
            while (router.SubscribedSentinelCount < 1)
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
            if (router.NotificationRediscovery is { } reconciliation)
                await reconciliation.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        await Verify().ConfigureAwait(false);
        ReportMemory("connected-idle");
        // Bounded sequential churn is outside the timed region.
        for (var i = 0; i < 256; i++)
            await Verify().ConfigureAwait(false);
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
    public async Task<int> Batch()
    {
        using var batch = _client.CreateBatch();
        var first = batch.Exists(_first);
        var second = batch.Exists(_second);
        await batch.ExecuteAsync().ConfigureAwait(false);
        return (first.Result ? 1 : 0) + (second.Result ? 2 : 0);
    }

    [Benchmark]
    public async Task<int> Transaction()
    {
        await using var transaction = _client.CreateTransaction();
        var first = transaction.Exists(_first);
        var second = transaction.Exists(_second);
        await transaction.CommitAsync().ConfigureAwait(false);
        return (first.Result ? 1 : 0) + (second.Result ? 2 : 0);
    }

    private async Task Verify()
    {
        var result = _method switch
        {
            nameof(Batch) => await Batch().ConfigureAwait(false),
            nameof(Transaction) => await Transaction().ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown unit retry benchmark method."),
        };
        if (result != 1)
            throw new InvalidOperationException("Unit retry fixture returned an unexpected result.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _client = null!;
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
            stage, owner = Owner, policy = Policy, method = _method, managedBytes = GC.GetTotalMemory(false),
            pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes,
            workingSetBytes = process.WorkingSet64,
        }));
    }
}
