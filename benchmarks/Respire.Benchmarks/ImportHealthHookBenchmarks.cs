using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Compare unconfigured import/diagnostic hook paths against the same fixture on the base revision.</summary>
[MemoryDiagnoser]
public class ImportHealthHookBenchmarks
{
    private IContainer? _container;
    private RespireClient _client = null!;
    private RespireHashImportSession _session = null!;
    private readonly RespireKey _key = "hooks:import";
    private readonly RespireValue _schema = "schema";
    private readonly RespireValue[] _fields = ["field"];
    private readonly RespireValue[] _values = ["value"];
    private readonly (RespireKey, RespireValue)[] _pairs = [("hooks:bulk:first", "one"), ("hooks:bulk:second", "two")];

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort) ? configuredPort : 6379;
        if (string.IsNullOrEmpty(host))
        {
            _container = new RedisBuilder("redis:8.10").Build();
            await _container.StartAsync();
            host = _container.Hostname;
            port = _container.GetMappedPublicPort(6379);
        }
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(host, port)], Protocol = RespireProtocol.Resp2, Connections = 1,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            CommandHook = null,
        });
        _session = await _client.Hashes.CreateImportSessionAsync();
        await _session.PrepareAsync(_schema, _fields);
        await _client.Strings.SetManyAsync(_pairs);
        // Stable bounded state: SET overwrites one hash; MSETNX always finds existing keys.
        if (!await ImportSet() || await BulkSetIfAbsent()
            || (await HealthProbe()).Any(result => result.Error is not null))
            throw new InvalidOperationException("Import/health fixture validation failed.");
        await OpenAndDisposeSession();
    }

    [Benchmark]
    public ValueTask<bool> ImportSet() => _session.SetAsync(_key, _schema, _values);

    [Benchmark]
    public ValueTask<bool> BulkSetIfAbsent() => _client.Strings.SetManyIfNotExistsAsync(_pairs, CancellationToken.None);

    [Benchmark]
    public ValueTask<RespireHealthProbeResult[]> HealthProbe() => _client.ProbeHealthAsync();

    // Acquisition includes its mandatory connection close; no leases accumulate between invocations.
    [Benchmark]
    public async ValueTask OpenAndDisposeSession()
    {
        await using var session = await _client.Hashes.CreateImportSessionAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_session is not null) await _session.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
