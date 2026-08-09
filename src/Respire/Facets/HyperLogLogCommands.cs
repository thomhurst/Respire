using Respire.Commands;

namespace Respire;

public interface IHyperLogLogCommands
{
    /// <summary>Adds values and returns whether estimated cardinality changed. Redis: PFADD.</summary>
    ValueTask<bool> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Returns estimated cardinality across one or more HyperLogLogs. Redis: PFCOUNT.</summary>
    ValueTask<long> CountAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Merges HyperLogLogs into <paramref name="destination"/>. Redis: PFMERGE.</summary>
    ValueTask MergeAsync(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys);
}

internal sealed class HyperLogLogCommands(RespireClient client) : IHyperLogLogCommands
{
    public ValueTask<bool> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => client.FlagAsync(
            "PFADD",
            new Cmd1N(RespireCommands.HyperLogLog.PFADD.Verb, client.Key(in key), values.ToArray()),
            CancellationToken.None);

    public ValueTask<long> CountAsync(params ReadOnlySpan<RespireKey> keys)
    {
        if (keys.IsEmpty)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }

        return client.IntegerAsync(
            "PFCOUNT", new CmdN(RespireCommands.HyperLogLog.PFCOUNT.Verb, client.MapKeys(keys)), CancellationToken.None);
    }

    public ValueTask MergeAsync(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys)
    {
        if (sourceKeys.IsEmpty)
        {
            throw new ArgumentException("At least one source key is required.", nameof(sourceKeys));
        }

        return client.OkAsync(
            "PFMERGE",
            new Cmd1N(RespireCommands.HyperLogLog.PFMERGE.Verb, client.Key(in destination), client.MapKeys(sourceKeys)),
            CancellationToken.None);
    }
}
