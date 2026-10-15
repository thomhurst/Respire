using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Streaming;

namespace Respire.Benchmarks;

/// <summary>One ungrouped enqueue through a hosted worker and its durable completion event.</summary>
[MemoryDiagnoser]
public class JobQueueBenchmarks
{
    // Include identical bounded cleanup in both builds: no retained stream or deduplication growth.
    private static readonly RespireScript Reset = RespireScript.Create("""
        redis.call('DEL', KEYS[3])
        redis.call('XDEL', KEYS[1], ARGV[1])
        redis.call('XDEL', KEYS[2], ARGV[2])
        return 1
        """);
    private readonly string _jobId = "job";
    private readonly string _payload = new('x', 128);
    private readonly CancellationTokenSource _stop = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private RespireClient _client = null!;
    private ServiceProvider _provider = null!;
    private IHostedService _worker = null!;
    private RespireJobQueue<string> _queue = null!;
    private IAsyncEnumerator<RespireJobEvent> _events = null!;
    private RespireKey[] _resetKeys = null!;
    private TimeSpan _cpu;
    private long _started;
    private long _operations;

    [Params(false, true)]
    public bool SchedulingEnabled { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"))],
            Connections = 1, Protocol = RespireProtocol.Resp3,
        });
        var name = "benchmark-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection().AddSingleton<IRespireClient>(_client);
        services.AddRespireJobQueue<Handler, string>(name, Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ConsumerName = "benchmark", ConsumerCount = 1, BatchSize = 1 },
            SchedulingEnabled ? new RespireJobQueueOptions() : null);
        _provider = services.BuildServiceProvider();
        _queue = _provider.GetRequiredKeyedService<RespireJobQueue<string>>(name);
        _worker = _provider.GetServices<IHostedService>().Single();
        _resetKeys = [_queue.StreamKey, _queue.EventsStreamKey,
            _queue.StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_jobId)))];
        _events = _queue.ReadEventsAsync(cancellationToken: _stop.Token).GetAsyncEnumerator();
        await _worker.StartAsync(_stop.Token);
        await EnqueueAndConsume(); // Warm scripts, group creation, dedicated readers and serializers.
        _operations = 0;
        _process.Refresh();
        _cpu = _process.TotalProcessorTime;
        _started = Stopwatch.GetTimestamp();
    }

    [Benchmark]
    public async Task<long> EnqueueAndConsume()
    {
        var enqueued = await _queue.EnqueueAsync(_jobId, _payload, cancellationToken: _stop.Token);
        if (!enqueued.Enqueued) throw new InvalidOperationException("Benchmark job was deduplicated.");
        if (!await _events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("Completion stream ended.");
        var completed = _events.Current;
        if (completed.Kind != "completed" || completed.JobId != _jobId || completed.EntryId != enqueued.EntryId)
            throw new InvalidOperationException("The worker did not durably complete this benchmark job.");
        using var reset = await _client.Scripts.ExecuteAsync(Reset, _resetKeys,
            [enqueued.EntryId.ToString(), completed.Id.ToString()], _stop.Token);
        _operations++;
        return reset.AsInteger();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _process.Refresh();
        Console.WriteLine("JOB_QUEUE_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            SchedulingEnabled, operations = _operations,
            cpuMilliseconds = (_process.TotalProcessorTime - _cpu).TotalMilliseconds,
            elapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
        }));
        _stop.Cancel();
        await _worker.StopAsync(CancellationToken.None);
        await _events.DisposeAsync();
        await _provider.DisposeAsync();
        foreach (var key in _resetKeys) await _client.DeleteAsync(key);
        await _client.DisposeAsync();
        _stop.Dispose();
        _process.Dispose();
    }

    public sealed class Handler : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            if (job.Group is not null || job.Payload.Length != 128)
                throw new InvalidOperationException("Unexpected benchmark payload.");
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }
}
