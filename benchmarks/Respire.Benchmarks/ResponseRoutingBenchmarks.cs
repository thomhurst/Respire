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
        if (Route == "Replica") await VerifyReplicaRouteAsync(timeout.Token);
    }

    private async Task VerifyReplicaRouteAsync(CancellationToken cancellationToken)
    {
        // This isolated topology has no other GET producer during setup. Observe each
        // server directly so a silent fallback to the primary cannot validate the fixture.
        await using var primary = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = [new("127.0.0.1", 19580)] });
        await using var replica = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = [new("127.0.0.1", 19581)] });
        var primaryBefore = await GetCallsAsync(primary, cancellationToken);
        var replicaBefore = await GetCallsAsync(replica, cancellationToken);
        if (await _client.GetStringAsync(Key, cancellationToken) != Value)
            throw new InvalidOperationException("Replica route returned an unexpected value.");
        var primaryAfter = await GetCallsAsync(primary, cancellationToken);
        var replicaAfter = await GetCallsAsync(replica, cancellationToken);
        if (primaryAfter != primaryBefore || replicaAfter != replicaBefore + 1)
            throw new InvalidOperationException($"Replica route mismatch: primary GET delta {primaryAfter - primaryBefore}, replica GET delta {replicaAfter - replicaBefore}.");
    }

    private static async Task<long> GetCallsAsync(RespireClient client, CancellationToken cancellationToken)
    {
        using var info = await client.ExecuteAsync("INFO", ["commandstats"], cancellationToken: cancellationToken);
        const string prefix = "cmdstat_get:calls=";
        foreach (var line in info.AsString().Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return long.Parse(line.AsSpan(prefix.Length, line.IndexOf(',') - prefix.Length), System.Globalization.CultureInfo.InvariantCulture);
        }
        return 0;
    }

    [Benchmark]
    public ValueTask<string?> GetString() => _client.GetStringAsync(Key);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
