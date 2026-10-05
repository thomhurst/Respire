using Respire.Commands;
using Respire.Internal;

namespace Respire;

public partial interface IBatchSetCommands
{
    /// <summary>Queues the set difference count. Redis 8.10+: SDIFFCARD.</summary>
    RespirePending<long> DifferenceCount(params ReadOnlySpan<RespireKey> keys);
    /// <summary>Queues the set difference count up to a nonnegative limit; zero means unlimited.</summary>
    RespirePending<long> DifferenceCount(long limit, params ReadOnlySpan<RespireKey> keys);
    /// <summary>Queues the distinct union count. Redis 8.10+: SUNIONCARD.</summary>
    RespirePending<long> UnionCount(params ReadOnlySpan<RespireKey> keys);
    /// <summary>Queues the union count with approximation and limit options. Redis 8.10+: SUNIONCARD.</summary>
    /// <remarks>Key sequences are snapshotted; byte-backed keys borrow their storage until execution completes.</remarks>
    RespirePending<long> UnionCount(RespireSetUnionCountOptions options, params ReadOnlySpan<RespireKey> keys);
}

internal sealed partial class BatchSetCommands
{
    public RespirePending<long> DifferenceCount(params ReadOnlySpan<RespireKey> keys) => DifferenceCount(0, keys);
    public RespirePending<long> DifferenceCount(long limit, params ReadOnlySpan<RespireKey> keys)
        => sink.Add<CmdN, long>("SDIFFCARD", SetCommands.CardinalityCommand(sink.Client, keys, limit, union: false),
            static (c, v) => ResponseReader.Integer(in v));
    public RespirePending<long> UnionCount(params ReadOnlySpan<RespireKey> keys) => UnionCount(default, keys);
    public RespirePending<long> UnionCount(RespireSetUnionCountOptions options, params ReadOnlySpan<RespireKey> keys)
        => sink.Add<CmdN, long>("SUNIONCARD", SetCommands.CardinalityCommand(sink.Client, keys, options.Limit,
            union: true, options.Approximate), static (c, v) => ResponseReader.Integer(in v));
}
