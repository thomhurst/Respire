using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// HyperLogLog commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IRespireHyperLogLogCommands"/>.
/// </summary>
public interface IRespireBatchHyperLogLogCommands
{
    /// <summary>Queues adding values to a HyperLogLog. Redis: PFADD.</summary>
    /// <returns>A pending result that resolves to <see langword="true"/> if the sketch was created or updated,
    /// otherwise <see langword="false"/>.</returns>
    /// <remarks>The estimated cardinality can stay the same when the sketch changes.</remarks>
    RespirePending<bool> Add(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Returns estimated cardinality across one or more HyperLogLogs. Redis: PFCOUNT.</summary>
    RespirePending<long> Count(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Merges HyperLogLogs into <paramref name="destination"/>; true once the server replies OK. Redis: PFMERGE.</summary>
    RespirePending<bool> Merge(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys);
}

internal sealed class BatchHyperLogLogCommands(IPendingSink sink) : IRespireBatchHyperLogLogCommands
{
    public RespirePending<bool> Add(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => sink.Add<Cmd1N, bool>(
            "PFADD",
            new Cmd1N(RespireCommands.HyperLogLog.PFADD.Verb, sink.Client.Key(in key), values.ToArray()),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> Count(params ReadOnlySpan<RespireKey> keys)
    {
        return sink.Add<CmdN, long>(
            "PFCOUNT",
            HyperLogLogCommands.CountCommand(sink.Client, keys),
            keys,
            static (c, v) => ResponseReader.Integer(in v));
    }

    public RespirePending<bool> Merge(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys)
    {
        return sink.Add<Cmd1N, bool>(
            "PFMERGE",
            SingleSlotCommand.Create(sink.Client, "PFMERGE", RespireCommands.HyperLogLog.PFMERGE.Verb,
                destination, sourceKeys),
            destination, sourceKeys,
            static (c, v) => ResponseReader.Ok(in v));
    }
}
