using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

public partial interface ISortedSetCommands
{
    /// <summary>One random UTF-8 member, or null when absent. Redis: ZRANDMEMBER (6.2+).</summary>
    ValueTask<string?> RandomMemberAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>One random deserialized member, or default when absent. Redis: ZRANDMEMBER (6.2+).</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> RandomMemberAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Random members. Positive count returns distinct members; negative count permits duplicates; zero returns empty. Redis: ZRANDMEMBER (6.2+).</summary>
    ValueTask<string[]> RandomMembersAsync(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>Random deserialized members. Negative count permits duplicates. Redis: ZRANDMEMBER (6.2+).</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T[]> RandomMembersAsync<T>(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>Random members with scores. Positive count returns distinct members; negative count permits duplicates; zero returns empty. Redis: ZRANDMEMBER (6.2+).</summary>
    ValueTask<SortedSetEntry[]> RandomMembersWithScoresAsync(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>Random deserialized members with scores. Negative count permits duplicates. Redis: ZRANDMEMBER (6.2+).</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<SortedSetEntry<T>[]> RandomMembersWithScoresAsync<T>(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>Counts members within a lexicographical range. All members must have the same score. Redis: ZLEXCOUNT.</summary>
    ValueTask<long> CountByLexAsync(RespireKey key, RespireLexRange range, CancellationToken cancellationToken = default);

    /// <summary>Removes members within a lexicographical range. All members must have the same score. Redis: ZREMRANGEBYLEX.</summary>
    ValueTask<long> RemoveRangeByLexAsync(RespireKey key, RespireLexRange range, CancellationToken cancellationToken = default);

    /// <summary>Intersection size, capped by a nonnegative limit (zero means unlimited). Keys must share a Cluster slot. Redis: ZINTERCARD (7.0+).</summary>
    ValueTask<long> IntersectCountAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Intersection size, capped by a nonnegative limit (zero means unlimited). Keys must share a Cluster slot. Redis: ZINTERCARD (7.0+).</summary>
    ValueTask<long> IntersectCountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Intersection size, capped by a nonnegative limit (zero means unlimited). Keys must share a Cluster slot. Redis: ZINTERCARD (7.0+).</summary>
    ValueTask<long> IntersectCountAsync(long limit, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Intersection size, capped by a nonnegative limit (zero means unlimited). Keys must share a Cluster slot. Redis: ZINTERCARD (7.0+).</summary>
    ValueTask<long> IntersectCountAsync(long limit, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);
}

internal sealed partial class SortedSetCommands
{
    public ValueTask<string?> RandomMemberAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("ZRANDMEMBER", new Cmd1(RespireCommands.SortedSet.ZRANDMEMBER.Verb, client.Key(in key)), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> RandomMemberAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, Cmd1>("ZRANDMEMBER", new Cmd1(RespireCommands.SortedSet.ZRANDMEMBER.Verb, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> RandomMembersAsync(RespireKey key, long count, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("ZRANDMEMBER", RandomMembersCommand(client, key, count), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RandomMembersAsync<T>(RespireKey key, long count, CancellationToken cancellationToken = default)
        => client.DeserializeArrayAsync<T, Cmd2>("ZRANDMEMBER", RandomMembersCommand(client, key, count), cancellationToken);

    public ValueTask<SortedSetEntry[]> RandomMembersWithScoresAsync(RespireKey key, long count, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("ZRANDMEMBER", RandomScoredMembersCommand(client, key, count), cancellationToken, this,
            static (SortedSetCommands _, in RespValue reply) => ParseEntries(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<SortedSetEntry<T>[]> RandomMembersWithScoresAsync<T>(RespireKey key, long count, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("ZRANDMEMBER", RandomScoredMembersCommand(client, key, count), cancellationToken, client,
            static (RespireClient c, in RespValue reply) => ParseEntries<T>(c, in reply));

    internal static Cmd2 RandomMembersCommand(RespireClient client, RespireKey key, long count)
    {
        ValidateRandomCount(count);
        return new Cmd2(RespireCommands.SortedSet.ZRANDMEMBER.Verb, client.Key(in key), count);
    }

    internal static Cmd3 RandomScoredMembersCommand(RespireClient client, RespireKey key, long count)
    {
        ValidateRandomCount(count);
        return new Cmd3(RespireCommands.SortedSet.ZRANDMEMBER.Verb, client.Key(in key), count, "WITHSCORES");
    }

    private static void ValidateRandomCount(long count)
    {
        if (count == long.MinValue)
            throw new ArgumentOutOfRangeException(nameof(count), "The absolute count must fit in a signed 64-bit integer.");
    }

    public ValueTask<long> CountByLexAsync(RespireKey key, RespireLexRange range, CancellationToken cancellationToken = default)
        => client.IntegerAsync("ZLEXCOUNT", new Cmd3(RespireCommands.SortedSet.ZLEXCOUNT.Verb,
            client.Key(in key), range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue()), cancellationToken);

    public ValueTask<long> RemoveRangeByLexAsync(RespireKey key, RespireLexRange range, CancellationToken cancellationToken = default)
        => client.IntegerAsync("ZREMRANGEBYLEX", new Cmd3(RespireCommands.SortedSet.ZREMRANGEBYLEX.Verb,
            client.Key(in key), range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue()), cancellationToken);

    public ValueTask<long> IntersectCountAsync(params ReadOnlySpan<RespireKey> keys)
        => IntersectCountAsync(0, keys, CancellationToken.None);

    public ValueTask<long> IntersectCountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => IntersectCountAsync(0, keys, cancellationToken);

    public ValueTask<long> IntersectCountAsync(long limit, params ReadOnlySpan<RespireKey> keys)
        => IntersectCountAsync(limit, keys, CancellationToken.None);

    public ValueTask<long> IntersectCountAsync(long limit, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.IntegerAsync("ZINTERCARD", IntersectCountCommand(client, keys, limit), cancellationToken);

    internal static CmdN IntersectCountCommand(RespireClient client, ReadOnlySpan<RespireKey> keys, long limit)
    {
        var arguments = CountedKeyArguments.Create(client, keys, limit);
        if (client.Core.Cluster is not null)
        {
            int? slot = null;
            for (var i = 1; i <= keys.Length; i++)
            {
                if (!arguments[i].TryGetClusterSlot(out var current)) continue;
                if (slot is { } expected && expected != current)
                    throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", "ZINTERCARD");
                slot = current;
            }
        }
        return new CmdN(Verbs.ZInterCard, arguments);
    }
}
