using BenchmarkDotNet.Attributes;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Success-path coverage for timed batches and transactions on a real Redis connection.</summary>
[MemoryDiagnoser]
public class DiagnosticsPipelineBenchmarks
{
    private const int CommandCount = 32;
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.2-alpine").Build();
    private readonly RespirePending<long>[] _pending = new RespirePending<long>[CommandCount];
    private readonly RespireKey _key = "diagnostics-benchmark";
    private RespireClient _client = null!;
    private ErrorMetricBenchmarkScope _errorMetrics = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _errorMetrics = new();
        await _redis.StartAsync();
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(_redis.Hostname, _redis.GetMappedPublicPort(6379)) },
            Connections = 1, CommandTimeout = TimeSpan.FromSeconds(5)
        });
        await _client.SetAsync(_key, "value");
        await _errorMetrics.WarmAsync(_client, _key);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        await _redis.DisposeAsync();
        _errorMetrics.Dispose();
    }

    // Each invocation is one application batch; report time/allocation per command.
    [Benchmark(OperationsPerInvoke = CommandCount)]
    public async Task<long> PipelinedBatch()
    {
        using var batch = _client.CreateBatch();
        for (var i = 0; i < CommandCount; i++) _pending[i] = batch.Strings.Length(_key);
        await batch.ExecuteAsync();
        return ReadResults();
    }

    [Benchmark(OperationsPerInvoke = CommandCount)]
    public async Task<long> TimedTransaction()
    {
        await using var transaction = _client.CreateTransaction();
        for (var i = 0; i < CommandCount; i++) _pending[i] = transaction.Strings.Length(_key);
        await transaction.CommitAsync();
        return ReadResults();
    }

    [Benchmark(OperationsPerInvoke = CommandCount)]
    public async Task<int> PipelinedBatchServerErrors()
    {
        using var batch = _client.CreateBatch();
        for (var i = 0; i < CommandCount; i++) _pending[i] = batch.Strings.Increment(_key);
        try { await batch.ExecuteAsync(); }
        catch (RespireServerException error) when (error.Code == "ERR") { }
        var errors = 0;
        foreach (var pending in _pending)
        {
            try { _ = pending.Result; }
            catch (RespireServerException error) when (error.Code == "ERR") { errors++; }
        }
        if (errors != CommandCount) throw new InvalidOperationException("Expected one Redis ERR per command.");
        return errors;
    }

    private long ReadResults()
    {
        long total = 0;
        foreach (var result in _pending) total += result.Result;
        if (total != CommandCount * 5) throw new InvalidOperationException("Unexpected Redis result.");
        return total;
    }
}
