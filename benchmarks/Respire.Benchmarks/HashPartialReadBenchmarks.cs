using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

/// <summary>Measures a fixed batch of independent hashes with a controlled field-hit ratio.</summary>
[MemoryDiagnoser]
[InvocationCount(1)]
public class HashPartialReadBenchmarks
{
    private const int HashCount = 64;
    private readonly string[] _keys = Enumerable.Range(0, HashCount)
        .Select(index => $"respire:hash-partial:{Guid.NewGuid():N}:{index}").ToArray();
    private readonly string[] _fields = ["a", "b", "c", "d"];
    private readonly string _value = new('x', 64);
    private RespireClient _client = null!;
    private string[] _primedFields = [];
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _cpuAt;
    private long _startedAt;
    private long _reads;

    [Params(0, 2, 4)]
    public int CachedFields { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379");
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new(host, port) }, Connections = 1,
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        foreach (var key in _keys)
            foreach (var field in _fields)
                await _client.Hashes.SetAsync(key, field, _value);
        _primedFields = _fields[..CachedFields];
        var values = await _client.Hashes.GetManyAsync(_keys[0], _fields);
        if (values.Length != 4 || values.Any(value => value != _value))
            throw new InvalidOperationException("Hash fixture did not return the expected values.");
        _process.Refresh();
        _cpuAt = _process.TotalProcessorTime;
        _startedAt = Stopwatch.GetTimestamp();
    }

    [IterationSetup]
    public void PrimeFields()
    {
        // BDN performs one invocation per iteration. Each measured read uses a separate
        // hash, so no earlier operation turns a planned miss into a hit. Priming is excluded.
        _client.ClientSideCache!.Clear();
        if (CachedFields == 0) return;
        foreach (var key in _keys)
            _client.Hashes.GetManyAsync(key, _primedFields).AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(OperationsPerInvoke = HashCount)]
    public async Task<int> ReadBatch()
    {
        var count = 0;
        foreach (var key in _keys)
        {
            var values = await _client.Hashes.GetManyAsync(key, _fields);
            count += values.Length;
            _reads++;
        }
        return count;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _process.Refresh();
        Console.WriteLine("HASH_PARTIAL_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            CachedFields, reads = _reads, elapsedSeconds = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
            cpuMilliseconds = (_process.TotalProcessorTime - _cpuAt).TotalMilliseconds
        }));
        foreach (var key in _keys) await _client.DeleteAsync(key);
        await _client.DisposeAsync();
        _process.Dispose();
    }
}
