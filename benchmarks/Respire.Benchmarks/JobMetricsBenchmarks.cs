using System.Reflection;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Respire.Streaming;

namespace Respire.Benchmarks;

/// <summary>Real terminal completion scripts, comparing disabled metrics with a 60-minute window.</summary>
[MemoryDiagnoser]
public class JobMetricsBenchmarks
{
    // Identical preparation is measured in both methods. Recreate a single pending delivery
    // and clear its previous event, so BDN calibration cannot grow either stream without bound.
    private static readonly RespireScript Prepare = RespireScript.Create("""
        redis.call('DEL', KEYS[1], KEYS[2])
        redis.call('XADD', KEYS[1], '1-0', '_respire.job.id', 'job', '_respire.job.payload', 'payload')
        redis.call('XGROUP', 'CREATE', KEYS[1], 'workers', '0')
        redis.call('XREADGROUP', 'GROUP', 'workers', 'benchmark', 'COUNT', 1, 'STREAMS', KEYS[1], '>')
        return 1
        """);
    private readonly RespireValue[] _disabledArguments =
        ["workers", "benchmark", "1-0", 1, "completed", "", "", 0, Array.Empty<byte>(), 0];
    private readonly RespireValue[] _enabledArguments =
        ["workers", "benchmark", "1-0", 1, "completed", "", "", 0, Array.Empty<byte>(), 60];
    private RespireClient _client = null!;
    private RespireScript _disabled = null!;
    private RespireScript _enabled = null!;
    private RespireKey[] _disabledKeys = null!;
    private RespireKey[] _enabledKeys = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var scripts = typeof(RespireJobQueueOptions).Assembly.GetType("Respire.Streaming.JobQueueScripts", throwOnError: true)!;
        _disabled = (RespireScript)scripts.GetField("Complete", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _enabled = (RespireScript)scripts.GetField("CompleteWithMetrics", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var stream = "{job-metrics-" + Guid.NewGuid().ToString("N") + "}:stream";
        _disabledKeys = [stream, stream + ":events"];
        _enabledKeys = [stream, stream + ":events", stream + ":metrics"];
        _client = await Connect();
        await Disabled();
        if ((await RespireJobMetrics.ReadAsync(_client, stream)).Count != 0)
            throw new InvalidOperationException("Disabled completion created metrics.");
        await Enabled();
        var buckets = await RespireJobMetrics.ReadAsync(_client, stream);
        if (buckets.Sum(bucket => bucket.Completed) != 1 || buckets.Sum(bucket => bucket.Failed) != 0)
            throw new InvalidOperationException("Enabled completion did not increment the completed counter.");

        // Audit separately: the timing client has no hooks. These are logical command counts;
        // the warmed script cache excludes the normal first-use NOSCRIPT fallback.
        var observer = new CommandCounter();
        await using var auditClient = await Connect(observer);
        await Audit(auditClient, observer, enabled: false);
        await Audit(auditClient, observer, enabled: true);
    }

    [Benchmark(Baseline = true)]
    public Task<long> Disabled() => Complete(_disabled, _disabledKeys, _disabledArguments);

    [Benchmark]
    public Task<long> Enabled() => Complete(_enabled, _enabledKeys, _enabledArguments);

    private async Task<long> Complete(RespireScript script, RespireKey[] keys, RespireValue[] arguments)
    {
        using var prepared = await _client.Scripts.ExecuteAsync(Prepare, _disabledKeys);
        using var completed = await _client.Scripts.ExecuteAsync(script, keys, arguments);
        var acknowledged = completed.AsInteger();
        if (acknowledged != 1) throw new InvalidOperationException("The benchmark delivery was not acknowledged.");
        return acknowledged;
    }

    private async Task Audit(RespireClient client, CommandCounter observer, bool enabled)
    {
        using var prepared = await client.Scripts.ExecuteAsync(Prepare, _disabledKeys);
        observer.Reset();
        using var completed = await client.Scripts.ExecuteAsync(enabled ? _enabled : _disabled,
            enabled ? _enabledKeys : _disabledKeys, enabled ? _enabledArguments : _disabledArguments);
        var commands = observer.Commands;
        if (completed.AsInteger() != 1 || commands != 1)
            throw new InvalidOperationException($"Expected one successful completion command; observed {commands}.");
        Console.WriteLine("JOB_METRICS_COMMAND_AUDIT " + JsonSerializer.Serialize(new
        {
            enabled, logicalCompletionCommands = commands,
        }));
    }

    private static ValueTask<RespireClient> Connect(RespireCommandHook? observer = null)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"))],
            Connections = 1, Protocol = RespireProtocol.Resp3,
            ThreadPoolMonitoring = false,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            CommandHook = observer,
        });

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is null) return;
        foreach (var key in _enabledKeys) await _client.DeleteAsync(key);
        await _client.DisposeAsync();
    }

    private sealed class CommandCounter : RespireCommandHook
    {
        private int _commands;
        public int Commands => Volatile.Read(ref _commands);
        public void Reset() => Interlocked.Exchange(ref _commands, 0);
        public override object? OnExecuting(RespireCommandContext context)
        {
            Interlocked.Increment(ref _commands);
            return null;
        }
        public override void OnCompleted(RespireCommandContext context, object? state,
            RespireCommandOutcome outcome, Exception? error) { }
    }
}
