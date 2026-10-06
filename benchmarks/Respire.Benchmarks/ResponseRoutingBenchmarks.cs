using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

/// <summary>
/// End-to-end string GET latency through the pooled conversion routes, with a native
/// standalone control. Start infra/response-routing.sh in the pinned Redis container
/// and publish ports 19580-19583, as in benchmark-response-routing.yml.
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ResponseRoutingBenchmarks
{
    private const string Key = "benchmark:response-routing";
    private const string Value = "pooled-response";
    private RespireClient _client = null!;

    [Params("Standalone", "Cluster", "Sentinel", "Replica")]
    public string Route { get; set; } = "Standalone";

    [GlobalSetup]
    public async Task Setup()
    {
        var options = Route switch
        {
            "Standalone" => new RespireOptions { Endpoints = [new("127.0.0.1", 19580)] },
            "Cluster" => new RespireOptions { Endpoints = [new("127.0.0.1", 19583)], UseCluster = true },
            "Sentinel" => new RespireOptions { Endpoints = [new("127.0.0.1", 19582)], SentinelPrimaryName = "benchmark" },
            "Replica" => new RespireOptions
            {
                Endpoints = [new("127.0.0.1", 19580)],
                ReplicaEndpoints = [new("127.0.0.1", 19581)],
                ReadFrom = RespireReadFrom.Replica,
            },
            _ => throw new InvalidOperationException($"Unknown route: {Route}"),
        };
        _client = await RespireClient.ConnectAsync(options);
        await _client.SetAsync(Key, Value);
        // Replica replication is asynchronous. Validate the actual measured route before timing.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (await _client.GetStringAsync(Key, timeout.Token) != Value)
            await Task.Delay(25, timeout.Token);
    }

    [Benchmark]
    public ValueTask<string?> GetString() => _client.GetStringAsync(Key);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
