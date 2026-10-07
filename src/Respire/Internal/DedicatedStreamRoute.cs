using Respire.Networking;

namespace Respire.Internal;

/// <summary>Value snapshot revalidated immediately before a streamed header is admitted.</summary>
/// <remarks>
/// <para>None (also the default value) permits direct connection sends. No delegate or boxed strategy is retained.</para>
/// <para>Carries owner, pool, connection, slot generation, and ASK identity by value. The streaming
/// writer validates it after the first source read and again after ASKING, immediately before
/// admitting the SET header. Publication checks stay in <see cref="ClientCore"/> and
/// <see cref="ClusterRouter"/>; a stopped pool is not the only invalidation signal.
/// DedicatedStreamRouteTests measures construction and validation for standalone, Cluster, and ASK
/// routes with no socket work in the measured interval, warm no-inline loops in the no-GC boundary,
/// and escaping closures as the positive allocation control.</para>
/// </remarks>
internal readonly struct DedicatedStreamRoute
{
    /// <summary>Direct connection send with no router-owned generation to validate.</summary>
    internal static DedicatedStreamRoute None => default;

    private readonly ClientCore? _core;
    private readonly ClusterRouter? _cluster;
    private readonly DedicatedConnectionPool? _pool;
    private readonly RespireConnection? _connection;
    private readonly int? _slot;
    private readonly ClusterRouter.StreamRouteVersion _version;
    private readonly bool _asking;

    internal DedicatedStreamRoute(ClientCore core, DedicatedConnectionPool pool, RespireConnection connection)
    {
        _core = core;
        _pool = pool;
        _connection = connection;
    }

    internal DedicatedStreamRoute(ClusterRouter cluster, DedicatedConnectionPool pool,
        RespireConnection connection, int? slot, ClusterRouter.StreamRouteVersion version, bool asking)
    {
        _cluster = cluster;
        _pool = pool;
        _connection = connection;
        _slot = slot;
        _version = version;
        _asking = asking;
    }

    internal bool IsCurrent()
    {
        if (_core is not null) return _core.IsDedicatedStreamRouteCurrent(_pool!, _connection!);
        return _cluster is null || !_pool!.IsStopping && _pool.IsMovingPublicationCurrent
            && _cluster.IsDedicatedStreamRouteCurrent(_slot, _version, _connection!, _asking ? _pool : null);
    }
}
