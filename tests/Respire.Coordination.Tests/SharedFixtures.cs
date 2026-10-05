using Respire.Testing.Containers;
using TUnit.Core.Interfaces;

namespace Respire.Coordination.Tests;

/// <summary>
/// Caps concurrency for wire tests that assert on wall-clock margins (short deadlines, Task.Delay,
/// Stopwatch). They tolerate a little company but not a saturated machine.
/// </summary>
public sealed class TimingSensitive : IParallelLimit
{
    public int Limit => 2;
}

/// <summary>
/// Serializes dedicated Cluster/Sentinel containers. Those topologies publish fixed host ports chosen
/// just before Docker binds them, so concurrent starts can race for the same ports.
/// </summary>
public sealed class ContainerTopology : IParallelLimit
{
    public int Limit => 1;
}

/// <summary>
/// One container for the whole test session. Tests must only use keys with a unique prefix
/// (<see cref="Key"/>) and must not kill, pause or reconfigure the server.
/// </summary>
public abstract class SharedRespireContainer : IAsyncInitializer, IAsyncDisposable
{
    private RespireContainerFixture? _fixture;

    protected abstract RespireContainerOptions Options { get; }

    public RespireContainerFixture Fixture =>
        _fixture ?? throw new InvalidOperationException("The shared container has not been initialized.");

    public async Task InitializeAsync() => _fixture = await RespireContainerFixture.StartAsync(Options);

    /// <summary>Fresh client options for the shared deployment.</summary>
    public RespireOptions CreateOptions() => Fixture.CreateOptions();

    /// <summary>A key name no other test uses on this shared server.</summary>
    public static string Key(string name) => $"{name}:{Guid.NewGuid():N}";

    /// <summary>A key prefix no other test uses on this shared server.</summary>
    public static string Prefix(string name) => $"{name}-{Guid.NewGuid():N}:";

    public async ValueTask DisposeAsync()
    {
        var fixture = _fixture;
        _fixture = null;
        if (fixture is not null) await fixture.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

public sealed class SharedRedis72 : SharedRespireContainer
{
    protected override RespireContainerOptions Options => new() { Image = "redis:7.2-alpine" };
}

public sealed class SharedRedis74 : SharedRespireContainer
{
    protected override RespireContainerOptions Options => new() { Image = "redis:7.4-alpine" };
}

public sealed class SharedRedis810 : SharedRespireContainer
{
    protected override RespireContainerOptions Options => new() { Image = "redis:8.10-alpine" };
}

/// <summary>
/// A shared Redis 7.4 Cluster. Its startup binds fixed host ports, so it is the only Cluster that
/// outlives a single test; dedicated Cluster/Sentinel tests run under <see cref="ContainerTopology"/>.
/// </summary>
public sealed class SharedRedis74Cluster : SharedRespireContainer
{
    protected override RespireContainerOptions Options => new()
    {
        Topology = RespireContainerTopology.Cluster,
        Image = "redis:7.4-alpine",
    };
}

/// <summary>
/// Key for tests that subscribe on <see cref="SharedRedis74Cluster"/> while another test detects
/// SUBSCRIBE activity by the cluster's ports.
/// </summary>
public static class SharedClusterSubscriptions
{
    public const string Key = "shared-cluster-subscriptions";
}
