using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// HyperLogLog commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IHyperLogLogCommands"/>.
/// </summary>
public interface IBatchHyperLogLogCommands
{
    /// <summary>Adds values and returns whether estimated cardinality changed. Redis: PFADD.</summary>
    RespirePending<bool> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Returns estimated cardinality across one or more HyperLogLogs. Redis: PFCOUNT.</summary>
    RespirePending<long> CountAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Merges HyperLogLogs into <paramref name="destination"/>; true once the server replies OK. Redis: PFMERGE.</summary>
    RespirePending<bool> MergeAsync(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys);
}

internal sealed class BatchHyperLogLogCommands(IPendingSink sink) : IBatchHyperLogLogCommands
{
    public RespirePending<bool> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => sink.Add<Cmd1N, bool>(
            "PFADD",
            new Cmd1N(RespireCommands.HyperLogLog.PFADD.Verb, sink.Client.Key(in key), values.ToArray()),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> CountAsync(params ReadOnlySpan<RespireKey> keys)
    {
        if (keys.IsEmpty)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }

        return sink.Add<CmdN, long>(
            "PFCOUNT",
            new CmdN(RespireCommands.HyperLogLog.PFCOUNT.Verb, sink.Client.MapKeys(keys)),
            keys,
            static (c, v) => ResponseReader.Integer(in v));
    }

    public RespirePending<bool> MergeAsync(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys)
    {
        if (sourceKeys.IsEmpty)
        {
            throw new ArgumentException("At least one source key is required.", nameof(sourceKeys));
        }

        return sink.Add<Cmd1N, bool>(
            "PFMERGE",
            new Cmd1N(
                RespireCommands.HyperLogLog.PFMERGE.Verb,
                sink.Client.Key(in destination),
                sink.Client.MapKeys(sourceKeys)),
            destination, sourceKeys,
            static (c, v) => ResponseReader.Ok(in v));
    }
}
