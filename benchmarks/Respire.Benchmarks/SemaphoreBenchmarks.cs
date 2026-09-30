using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Respire.Extensions.Coordination;

namespace Respire.Benchmarks;

/// <summary>Compares uncontended standard lease and semaphore permit acquire-release cost.</summary>
[MemoryDiagnoser]
public class SemaphoreBenchmarks
{
    private RespireClient _client = null!;
    private RespireKey _key;
    private RespireSemaphore _semaphore = null!;
    private readonly TimeSpan _duration = TimeSpan.FromSeconds(30);
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _cpu;
    private long _started;
    private long _operations;

    [ParamsSource(nameof(Modes))]
    public bool Semaphore { get; set; }

    public IEnumerable<bool> Modes =>
        Environment.GetEnvironmentVariable("RESPIRE_BENCH_BASELINE") == "1" ? [false] : [false, true];

    [GlobalSetup]
    public async Task Setup()
    {
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"))],
            Connections = 1, Protocol = RespProtocol.Resp3,
        });
        _key = $"{{{Guid.NewGuid():N}}}:semaphore";
        _semaphore = new RespireSemaphore(_client, _key, capacity: 1);
        await AcquireRelease();
        _operations = 0;
        _process.Refresh();
        _cpu = _process.TotalProcessorTime;
        _started = Stopwatch.GetTimestamp();
    }

    [Benchmark]
    public Task AcquireRelease()
    {
        _operations++;
        return Semaphore ? AcquireSemaphoreAsync() : AcquireStandardAsync();
    }

    private async Task AcquireStandardAsync()
    {
        await using var standard = await _client.Locks.AcquireAsync(_key, _duration);
        if (!standard.Acquired) throw new InvalidOperationException("Uncontended benchmark lease was not acquired.");
    }

    private async Task AcquireSemaphoreAsync()
    {
        await using var attempt = await _semaphore.TryAcquireAsync(_duration);
        if (!attempt.Acquired) throw new InvalidOperationException("Uncontended benchmark permit was not acquired.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _process.Refresh();
        Console.WriteLine("SEMAPHORE_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            Semaphore, operations = _operations,
            cpuMilliseconds = (_process.TotalProcessorTime - _cpu).TotalMilliseconds,
            elapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
        }));
        await _client.DeleteAsync(_key);
        await _client.DisposeAsync();
        _process.Dispose();
    }
}
