using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

/// <summary>Public healthy dispatch with default, disabled and enabled retry ownership.</summary>
[MemoryDiagnoser]
public class CommandRetryBenchmarks
{
    private RespireClient _client = null!;
    private RespireFailoverGroup _group = null!;
    private readonly RespireKey _key = "retry:acceptance";
    private readonly RespireValue[] _arguments = ["retry:acceptance"];
    private readonly RespireKey[] _keys = ["retry:acceptance"];
    private Func<IRespireClient, CancellationToken, ValueTask<long>> _groupOperation = null!;
    private readonly RespireScript _script = RespireScript.Create("return redis.call('STRLEN', KEYS[1])", readOnly: true)
        .WithRetryCategory(RespireCommandRetryCategory.ReadOnly);

    [Params("Default", "Disabled", "Enabled")]
    public string Policy { get; set; } = "Default";

    [GlobalSetup]
    public async Task Setup()
    {
        ReportMemory("before-connect");
        var options = new RespireOptions
        {
            Endpoints = { new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var port) ? port : 6379) },
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        };
        RespireRetryPolicy? policy = Policy switch
        {
            "Default" => null,
            "Disabled" => new() { MaxAttempts = 1 },
            "Enabled" => new(),
            _ => throw new InvalidOperationException("Unknown policy comparison."),
        };
        _client = await RespireClient.ConnectAsync(options with { RetryPolicy = policy });
        _group = await RespireFailoverGroup.ConnectAsync([new(options, 0)], new()
        { RetryPolicy = policy, ProbeInterval = TimeSpan.FromHours(1) });
        _groupOperation = (client, token) => client.Strings.LengthAsync(_key, token);
        await _client.SetAsync(_key, "value");
        await _client.Scripts.LoadAsync(_script);
        if (await TypedLength() != 5 || await CatalogLength() != 5 || await ScriptLength() != 5 || await GroupLength() != 5)
            throw new InvalidOperationException("Retry benchmark fixture returned an unexpected length.");
        ReportMemory("connected-idle");
        // Bounded sequential churn measures retained ownership outside the timed region.
        for (var i = 0; i < 256; i++)
        {
            await TypedLength();
            await CatalogLength();
            await ScriptLength();
            await GroupLength();
        }
        ReportMemory("after-churn");
    }

    [Benchmark] public ValueTask<long> TypedLength() => _client.Strings.LengthAsync(_key);

    [Benchmark]
    public async ValueTask<long> CatalogLength()
    {
        using var result = await _client.ExecuteAsync(RespireCommands.String.STRLEN, _arguments);
        return result.AsInteger();
    }

    [Benchmark] public ValueTask<long> ScriptLength() => _client.Scripts.ExecuteIntegerAsync(_script, _keys);

    [Benchmark]
    public ValueTask<long> GroupLength() => _group.ExecuteAsync(
        _groupOperation, RespireCommandRetryCategory.ReadOnly);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_group is not null) await _group.DisposeAsync();
        _client = null!;
        _group = null!;
        ReportMemory("after-dispose");
    }

    private void ReportMemory(string stage)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("RECEIVE_MEMORY " + JsonSerializer.Serialize(new
        {
            stage, policy = Policy, managedBytes = GC.GetTotalMemory(false),
            pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes,
            workingSetBytes = process.WorkingSet64,
        }));
    }
}
