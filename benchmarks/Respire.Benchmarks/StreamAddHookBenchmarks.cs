using BenchmarkDotNet.Attributes;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Bounded stream appends across ordinary, IDMP and legacy fallback dispatch.</summary>
[MemoryDiagnoser]
public class StreamAddHookBenchmarks
{
    private RedisContainer _server = null!;
    private RespireClient _client = null!;
    private readonly RespireKey _key = "hooks:stream";
    private readonly RespireStreamAddOptions _options = new() { MaxLength = 100, ApproximateTrim = false };
    private readonly (string Field, RespireValue Value)[] _fields = [("field", "value")];

    [Params("Ordinary", "Idempotent", "Fallback")]
    public string Route { get; set; } = "Ordinary";

    [Params(false, true)]
    public bool Hooks { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _server = new RedisBuilder(Route == "Fallback" ? "redis:7.4.2" : "redis:8.10").Build();
        await _server.StartAsync();
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(_server.Hostname, _server.GetMappedPublicPort(6379))],
            Connections = 1, Protocol = RespireProtocol.Resp2, ThreadPoolMonitoring = false,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            CommandHook = Hooks ? new Observer() : null,
            RetryPolicy = Route == "Ordinary" ? null : new()
            {
                InitialBackoff = TimeSpan.Zero,
                MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked,
            },
        });
        if (await Append() is null) throw new InvalidOperationException("XADD did not return an ID.");
    }

    [Benchmark]
    public ValueTask<RespireStreamId?> Append() => _client.Streams.AddAsync(_key, _options, _fields);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
    }

    private sealed class Observer : RespireCommandHook
    {
        public override object? OnExecuting(RespireCommandContext context) => null;
        public override void OnCompleted(RespireCommandContext context, object? state,
            RespireCommandOutcome outcome, Exception? error) { }
    }
}
