using Respire.Commands;

namespace Respire;

public partial interface IRespireBatchStreamCommands
{
    /// <summary>Removes entries under a reference policy with one outcome per ID. Redis 8.2+: XDELEX.</summary>
    RespirePending<RespireStreamDeletionResult[]> Remove(RespireKey key, RespireStreamReferencePolicy policy,
        params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Acknowledges group entries and conditionally removes them. Redis 8.2+: XACKDEL.</summary>
    RespirePending<RespireStreamDeletionResult[]> AcknowledgeAndRemove(RespireKey key, string group,
        RespireStreamReferencePolicy policy, params ReadOnlySpan<RespireStreamId> ids);
}

internal sealed partial class BatchStreamCommands
{
    public RespirePending<RespireStreamDeletionResult[]> Remove(RespireKey key, RespireStreamReferencePolicy policy,
        params ReadOnlySpan<RespireStreamId> ids)
        => sink.Add<Cmd1N, RespireStreamDeletionResult[]>("XDELEX",
            StreamCommands.BuildReferenceRemovalCommand(sink.Client, SnapshotKey(key), null, policy, ids),
            static (_, value) => StreamCommands.ParseDeletionResults(in value));

    public RespirePending<RespireStreamDeletionResult[]> AcknowledgeAndRemove(RespireKey key, string group,
        RespireStreamReferencePolicy policy, params ReadOnlySpan<RespireStreamId> ids)
    {
        ArgumentNullException.ThrowIfNull(group);
        return sink.Add<Cmd1N, RespireStreamDeletionResult[]>("XACKDEL",
            StreamCommands.BuildReferenceRemovalCommand(sink.Client, SnapshotKey(key), group, policy, ids),
            static (_, value) => StreamCommands.ParseDeletionResults(in value));
    }
}
