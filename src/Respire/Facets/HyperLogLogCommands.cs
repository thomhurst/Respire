using System.Runtime.CompilerServices;
using Respire.Commands;

namespace Respire;

/// <summary>Probabilistic cardinality commands. Redis: PFADD, PFCOUNT, PFMERGE.</summary>
public interface IRespireHyperLogLogCommands
{
    /// <summary>Adds a value to a HyperLogLog. Redis: PFADD.</summary>
    /// <returns><see langword="true"/> if the sketch was created or updated, otherwise <see langword="false"/>.</returns>
    /// <remarks>The estimated cardinality can stay the same when the sketch changes.</remarks>
    ValueTask<bool> AddAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    [OverloadResolutionPriority(1)]
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    [OverloadResolutionPriority(1)]
    ValueTask<bool> MergeAsync(RespireKey destination, RespireKey sourceKey, CancellationToken cancellationToken);

    /// <summary>Adds values to a HyperLogLog. Redis: PFADD.</summary>
    /// <returns><see langword="true"/> if the sketch was created or updated, otherwise <see langword="false"/>.</returns>
    /// <remarks>The estimated cardinality can stay the same when the sketch changes.</remarks>
    ValueTask<bool> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Adds values to a HyperLogLog. Redis: PFADD.</summary>
    /// <returns><see langword="true"/> if the sketch was created or updated, otherwise <see langword="false"/>.</returns>
    /// <remarks>The estimated cardinality can stay the same when the sketch changes.</remarks>
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

internal sealed class HyperLogLogCommands(RespireClient client) : IRespireHyperLogLogCommands
{
    /// <inheritdoc/>
    public ValueTask<bool> AddAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken)
        => AddAsync(key, [value], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    [OverloadResolutionPriority(1)]
    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken)
        => CountAsync([key], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    [OverloadResolutionPriority(1)]
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
        return client.IntegerAsync("PFCOUNT", CountCommand(client, keys), cancellationToken);
    }

    internal static CmdN CountCommand(RespireClient client, ReadOnlySpan<RespireKey> keys)
    {
        if (keys.IsEmpty)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }

        return SingleSlotCommand.Create(client, "PFCOUNT", RespireCommands.HyperLogLog.PFCOUNT.Verb, keys);
    }

    public ValueTask<bool> MergeAsync(RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys)
        => MergeAsync(destination, sourceKeys, CancellationToken.None);

    public ValueTask<bool> MergeAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> sourceKeys, CancellationToken cancellationToken)
    {
        return client.OkResultAsync(
            "PFMERGE",
            SingleSlotCommand.Create(client, "PFMERGE", RespireCommands.HyperLogLog.PFMERGE.Verb,
                destination, sourceKeys),
            cancellationToken);
    }
}
