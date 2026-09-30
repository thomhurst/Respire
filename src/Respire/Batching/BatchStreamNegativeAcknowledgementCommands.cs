using Respire.Commands;

namespace Respire;

public partial interface IBatchStreamCommands
{
    /// <summary>Releases pending entries, returning the aggregate count. Redis 8.8+: XNACK.</summary>
    RespirePending<long> NegativeAcknowledge(RespireKey key, RespireValue group, StreamNackMode mode,
        params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Releases pending entries with advanced counter/force options. Redis 8.8+: XNACK.</summary>
    RespirePending<long> NegativeAcknowledge(RespireKey key, RespireValue group, StreamNackMode mode,
        StreamNackOptions options, params ReadOnlySpan<RespireStreamId> ids);
}

internal sealed partial class BatchStreamCommands
{
    public RespirePending<long> NegativeAcknowledge(RespireKey key, RespireValue group, StreamNackMode mode,
        params ReadOnlySpan<RespireStreamId> ids) => NegativeAcknowledge(key, group, mode, default, ids);

    public RespirePending<long> NegativeAcknowledge(RespireKey key, RespireValue group, StreamNackMode mode,
        StreamNackOptions options, params ReadOnlySpan<RespireStreamId> ids)
        => sink.Add<Cmd1N, long>("XNACK", StreamCommands.BuildNackCommand(sink.Client, SnapshotKey(key), group, mode, options, ids),
            static (_, value) => StreamCommands.ParseNackCount(in value));
}
