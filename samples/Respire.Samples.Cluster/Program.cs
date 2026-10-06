using Respire;

var options = RespireOptions.Parse(Environment.GetEnvironmentVariable("RESPIRE_CONNECTION")
    ?? "127.0.0.1:7000,cluster=true");
if (!options.UseCluster) throw new ArgumentException("RESPIRE_CONNECTION must enable cluster=true.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
await using var redis = await RespireClient.ConnectAsync(options, timeout.Token);

var shards = await redis.Server.ClusterShardsAsync(timeout.Token);
Console.WriteLine($"Discovered {shards.Length} shards from one seed.");
foreach (var tag in new[] { "a", "b", "c" })
{
    // These hash tags land in the three ranges assigned by compose.yaml.
    RespireKey key = $"respire:cluster-sample:{{{tag}}}";
    var expected = Guid.NewGuid().ToString();
    await redis.SetAsync(key, (RespireValue)expected, expiry: TimeSpan.FromMinutes(2), cancellationToken: timeout.Token);
    var actual = await redis.GetStringAsync(key, timeout.Token);
    if (actual != expected) throw new InvalidOperationException($"Round trip failed for {key}.");
    Console.WriteLine($"PASS: {key}, slot {redis.ResolveKey(key).ClusterSlot}");
}
Console.WriteLine("Cluster sample completed; all three values round-tripped.");
