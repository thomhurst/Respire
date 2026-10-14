using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>Options for Redis 8.10+ SUNIONCARD.</summary>
/// <param name="Limit">A nonnegative counting limit; zero means unlimited.</param>
/// <param name="Approximate">Requests an estimated cardinality instead of an exact count.</param>
public readonly record struct RespireSetUnionCountOptions(long Limit = 0, bool Approximate = false);

public partial interface ISetCommands
{
    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> DifferenceCountAsync(RespireKey key, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> DifferenceCountAsync(long limit, RespireKey key, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> UnionCountAsync(RespireKey key, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> UnionCountAsync(RespireSetUnionCountOptions options, RespireKey key, CancellationToken cancellationToken);

    /// <summary>Counts members in the first set absent from all subsequent sets. Redis 8.10+: SDIFFCARD.</summary>
    ValueTask<long> DifferenceCountAsync(params ReadOnlySpan<RespireKey> keys);
    /// <summary>Counts the set difference with cancellation. Redis 8.10+: SDIFFCARD.</summary>
    ValueTask<long> DifferenceCountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);
    /// <summary>Counts the set difference up to a nonnegative limit; zero means unlimited. Redis 8.10+: SDIFFCARD LIMIT.</summary>
    ValueTask<long> DifferenceCountAsync(long limit, params ReadOnlySpan<RespireKey> keys);
    /// <summary>Counts the set difference up to a nonnegative limit with cancellation. Redis 8.10+: SDIFFCARD LIMIT.</summary>
    ValueTask<long> DifferenceCountAsync(long limit, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);
    /// <summary>Counts distinct members across all sets. Redis 8.10+: SUNIONCARD.</summary>
    ValueTask<long> UnionCountAsync(params ReadOnlySpan<RespireKey> keys);
    /// <summary>Counts distinct members across all sets with cancellation. Redis 8.10+: SUNIONCARD.</summary>
    ValueTask<long> UnionCountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);
    /// <summary>Counts the union with optional approximation and limit. Redis 8.10+: SUNIONCARD.</summary>
    ValueTask<long> UnionCountAsync(RespireSetUnionCountOptions options, params ReadOnlySpan<RespireKey> keys);
    /// <summary>Counts the union with options and cancellation. Redis 8.10+: SUNIONCARD.</summary>
    ValueTask<long> UnionCountAsync(RespireSetUnionCountOptions options, ReadOnlySpan<RespireKey> keys,
        CancellationToken cancellationToken);
}

internal sealed partial class SetCommands
{
    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> DifferenceCountAsync(RespireKey key, CancellationToken cancellationToken)
        => DifferenceCountAsync([key], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> DifferenceCountAsync(long limit, RespireKey key, CancellationToken cancellationToken)
        => DifferenceCountAsync(limit, [key], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> UnionCountAsync(RespireKey key, CancellationToken cancellationToken)
        => UnionCountAsync([key], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> UnionCountAsync(RespireSetUnionCountOptions options, RespireKey key, CancellationToken cancellationToken)
        => UnionCountAsync(options, [key], cancellationToken);


    public ValueTask<long> DifferenceCountAsync(params ReadOnlySpan<RespireKey> keys)
        => DifferenceCountAsync(0, keys, CancellationToken.None);
    public ValueTask<long> DifferenceCountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => DifferenceCountAsync(0, keys, cancellationToken);
    public ValueTask<long> DifferenceCountAsync(long limit, params ReadOnlySpan<RespireKey> keys)
        => DifferenceCountAsync(limit, keys, CancellationToken.None);
    public ValueTask<long> DifferenceCountAsync(long limit, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.IntegerAsync("SDIFFCARD", CardinalityCommand(client, keys, limit, union: false), cancellationToken);
    public ValueTask<long> UnionCountAsync(params ReadOnlySpan<RespireKey> keys)
        => UnionCountAsync(default, keys, CancellationToken.None);
    public ValueTask<long> UnionCountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => UnionCountAsync(default, keys, cancellationToken);
    public ValueTask<long> UnionCountAsync(RespireSetUnionCountOptions options, params ReadOnlySpan<RespireKey> keys)
        => UnionCountAsync(options, keys, CancellationToken.None);
    public ValueTask<long> UnionCountAsync(RespireSetUnionCountOptions options, ReadOnlySpan<RespireKey> keys,
        CancellationToken cancellationToken)
        => client.IntegerAsync("SUNIONCARD", CardinalityCommand(client, keys, options.Limit, union: true,
            options.Approximate), cancellationToken);

    internal static CmdN CardinalityCommand(RespireClient client, ReadOnlySpan<RespireKey> keys,
        long limit, bool union, bool approximate = false)
    {
        var arguments = CountedKeyArguments.Create(client, keys, limit, approximate);
        if (client.Core.Cluster is not null)
            RawCommandKeyLayouts.ValidateClusterKeys(union ? "SUNIONCARD" : "SDIFFCARD", arguments,
                client.Core.Options.IncludeCrossSlotKeyContext);
        return new(union ? Verbs.SUnionCard : Verbs.SDiffCard, arguments);
    }
}
