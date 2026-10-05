using Respire.Commands;
using Respire.Internal;

namespace Respire;

public partial interface IBatchStreamCommands
{
    /// <summary>Queues configuration of an existing stream. Returns true on OK. Redis 8.6+: XCFGSET.</summary>
    /// <remarks>At least one setting is required. Changing a value clears producer deduplication records.
    /// Validation happens before enqueueing; cancellation belongs to batch execution or transaction commit.</remarks>
    RespirePending<bool> Configure(RespireKey key, StreamConfigurationOptions options);
}

internal sealed partial class BatchStreamCommands
{
    public RespirePending<bool> Configure(RespireKey key, StreamConfigurationOptions options)
        => sink.Add<Cmd1N, bool>("XCFGSET", StreamCommands.BuildConfigurationCommand(sink.Client, SnapshotKey(key), options),
            static (_, value) => ResponseReader.Ok(in value));
}
