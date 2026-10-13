using Respire.Commands;

namespace Respire;

/// <summary>Probabilistic cardinality commands. Redis: PFADD, PFCOUNT, PFMERGE.</summary>
public interface IHyperLogLogCommands
{
    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<bool> AddAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<bool> MergeAsync(RespireKey destination, RespireKey sourceKey, CancellationToken cancellationToken);

    /// <summary>Adds values and returns whether estimated cardinality changed. Redis: PFADD.</summary>
    ValueTask<bool> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Adds values and returns whether estimated cardinality changed. Redis: PFADD.</summary>
    ValueTask<bool> AddAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken);

    /// <summary>Returns estimated cardinality across one or more HyperLogLogs. Redis: PFCOUNT.</summary>
    ValueTask<long> CountAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Returns estimated cardinality across one or more HyperLogLogs. Redis: PFCOUNT.</summary>
    ValueTask<long> CountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Merges HyperLogLogs into <paramref name="destination"/>; returns true on OK. Redis: PFMERGE.</summary>
    ValueTask<bool> MergeAsync(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys);

    /// <summary>Merges HyperLogLogs into <paramref name="destination"/>; returns true on OK. Redis: PFMERGE.</summary>
    ValueTask<bool> MergeAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> sourceKeys, CancellationToken cancellationToken);
}

internal sealed class HyperLogLogCommands(RespireClient client) : IHyperLogLogCommands
{
    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<bool> AddAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken)
        => AddAsync(key, [value], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken)
        => CountAsync([key], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<bool> MergeAsync(RespireKey destination, RespireKey sourceKey, CancellationToken cancellationToken)
        => MergeAsync(destination, [sourceKey], cancellationToken);


    public ValueTask<bool> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => AddAsync(key, values, CancellationToken.None);

    public ValueTask<bool> AddAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
        => client.FlagAsync(
            "PFADD",
            new Cmd1N(RespireCommands.HyperLogLog.PFADD.Verb, client.Key(in key), values.ToArray()),
            cancellationToken);

    public ValueTask<long> CountAsync(params ReadOnlySpan<RespireKey> keys)
        => CountAsync(keys, CancellationToken.None);

    public ValueTask<long> CountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
    {
        if (keys.IsEmpty)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }

        return client.IntegerAsync(
            "PFCOUNT", new CmdN(RespireCommands.HyperLogLog.PFCOUNT.Verb, client.MapKeys(keys)), cancellationToken);
    }

    public ValueTask<bool> MergeAsync(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys)
        => MergeAsync(destination, sourceKeys, CancellationToken.None);

    public ValueTask<bool> MergeAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> sourceKeys, CancellationToken cancellationToken)
    {
        if (sourceKeys.IsEmpty)
        {
            throw new ArgumentException("At least one source key is required.", nameof(sourceKeys));
        }

        return client.OkResultAsync(
            "PFMERGE",
            new Cmd1N(RespireCommands.HyperLogLog.PFMERGE.Verb, client.Key(in destination), client.MapKeys(sourceKeys)),
            cancellationToken);
    }
}
