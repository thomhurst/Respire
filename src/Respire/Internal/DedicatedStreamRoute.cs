using Respire.Networking;

namespace Respire.Internal;

/// <summary>Value snapshot revalidated immediately before a streamed header is admitted.</summary>
/// <remarks>None (also the default value) permits direct connection sends. No delegate or boxed strategy is retained.</remarks>
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
