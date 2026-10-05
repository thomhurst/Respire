using Respire.Internal;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

/// <summary>
/// Session-wide three-primary Redis Cluster. Tests that move slots must reserve their own with
/// <see cref="ReserveSlot"/> and share <see cref="ReshardingKey"/> in a keyed <c>NotInParallel</c>,
/// because a slot move changes the topology every client of the cluster observes.
/// </summary>
public class SharedRedisClusterFixture(string image, bool loadBloomModule) : IAsyncInitializer, IAsyncDisposable
{
    public const string ReshardingKey = "shared-redis-cluster-resharding";

    private readonly HashSet<int> _reservedSlots = [];
    private RedisClusterTestContainer? _cluster;

    public SharedRedisClusterFixture() : this("redis:7.0.15", loadBloomModule: false)
    {
    }

    internal RedisClusterTestContainer Cluster =>
        _cluster ?? throw new InvalidOperationException("The shared cluster has not started.");

    public async Task InitializeAsync() => _cluster = await RedisClusterTestContainer.StartAsync(image, loadBloomModule);

    /// <summary>Returns a hash tag for a slot that <paramref name="node"/> owns and no other test has reserved.</summary>
    public string ReserveSlot(int node)
    {
        var first = node * 5461;
        var last = node == 2 ? 16383 : first + 5460;
        lock (_reservedSlots)
        {
            for (var index = 0; ; index++)
            {
                var tag = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var slot = ClusterHash.GetSlot(tag);
                if (slot >= first && slot <= last && _reservedSlots.Add(slot)) return tag;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cluster = Interlocked.Exchange(ref _cluster, null);
        if (cluster is not null) await cluster.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A session-wide Redis 8 cluster with RedisBloom loaded, for probabilistic cluster routing.</summary>
public sealed class BloomRedisClusterFixture() : SharedRedisClusterFixture("redis:8.10-alpine", loadBloomModule: true);
