using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
#if FENCED_LOCKS
using Respire.Coordination;
#endif

namespace Respire.Benchmarks;

/// <summary>Uncontended acquire-and-release cost, including generated owner tokens and managed cleanup.</summary>
[MemoryDiagnoser]
public class FencedLockBenchmarks
{
    private RespireClient _client = null!;
    private RespireKey _key;
    private RespireKey _counter;
    private readonly TimeSpan _duration = TimeSpan.FromSeconds(30);
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _cpu;
    private long _started;
    private long _operations;
#if FENCED_LOCKS
    private RespireCoordination _coordination = null!;
#endif
    [ParamsSource(nameof(Modes))]
    public bool Fenced { get; set; }
    public IEnumerable<bool> Modes =>
#if FENCED_LOCKS
        Environment.GetEnvironmentVariable("RESPIRE_BENCH_BASELINE") == "1" ? [false] : [false, true];
#else
        [false];
#endif

    [GlobalSetup]
    public async Task Setup()
    {
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"))],
            Connections = 1, Protocol = RespProtocol.Resp3,
        });
        var tag = Guid.NewGuid().ToString("N");
        _key = $"{{{tag}}}:lease";
        _counter = $"{{{tag}}}:counter";
#if FENCED_LOCKS
        _coordination = new RespireCoordination(_client);
#endif
        await AcquireRelease(); // Resolve script/capability setup outside measurement.
        _operations = 0;
        _process.Refresh();
        _cpu = _process.TotalProcessorTime;
        _started = Stopwatch.GetTimestamp();
    }

    [Benchmark]
    public Task<long> AcquireRelease()
    {
        _operations++;
#if FENCED_LOCKS
        if (Fenced) return AcquireFencedAsync();
#endif
        return AcquireStandardAsync();
    }

    // Keep the standard async state machine identical in the baseline and candidate builds.
    // Conditional fenced-lease locals in one async method would inflate only the candidate control.
    private async Task<long> AcquireStandardAsync()
    {
        await using var standard = await _client.Locks.AcquireAsync(_key, _duration);
        if (!standard.Acquired) throw new InvalidOperationException("Uncontended benchmark lease was not acquired.");
        return 1;
    }

#if FENCED_LOCKS
    private async Task<long> AcquireFencedAsync()
    {
        await using var attempt = await _coordination.TryAcquireFencedLockAsync(_key, _counter, _duration);
        return attempt.Lock.FencingToken;
    }
#endif

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _process.Refresh();
        Console.WriteLine("FENCED_LOCK_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            Fenced, operations = _operations,
            cpuMilliseconds = (_process.TotalProcessorTime - _cpu).TotalMilliseconds,
            elapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
        }));
        // Unique benchmark-only keys; real applications must retain counter history.
        await _client.DeleteAsync(_key);
        await _client.DeleteAsync(_counter);
        await _client.DisposeAsync();
        _process.Dispose();
    }
}
