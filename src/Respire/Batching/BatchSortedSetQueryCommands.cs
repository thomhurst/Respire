using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

public partial interface IBatchSortedSetCommands
{
    /// <summary>One random UTF-8 member, or null when absent. Redis: ZRANDMEMBER (6.2+).</summary>
    RespirePending<string?> RandomMember(RespireKey key);

    /// <summary>One random deserialized member, or default when absent. Redis: ZRANDMEMBER (6.2+).</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> RandomMember<T>(RespireKey key);

    /// <summary>Random members. Positive count returns distinct members; negative count permits duplicates; zero returns empty. Redis: ZRANDMEMBER (6.2+).</summary>
    RespirePending<string[]> RandomMembers(RespireKey key, long count);

    /// <summary>Random deserialized members. Negative count permits duplicates. Redis: ZRANDMEMBER (6.2+).</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T[]> RandomMembers<T>(RespireKey key, long count);

    /// <summary>Random members with scores. Positive count returns distinct members; negative count permits duplicates; zero returns empty. Redis: ZRANDMEMBER (6.2+).</summary>
    RespirePending<SortedSetEntry[]> RandomMembersWithScores(RespireKey key, long count);

    /// <summary>Random deserialized members with scores. Negative count permits duplicates. Redis: ZRANDMEMBER (6.2+).</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<SortedSetEntry<T>[]> RandomMembersWithScores<T>(RespireKey key, long count);

    /// <summary>Counts members within a lexicographical range. All members must have the same score. Redis: ZLEXCOUNT.</summary>
    RespirePending<long> CountByLex(RespireKey key, RespireLexRange range);

    /// <summary>Removes members within a lexicographical range. All members must have the same score. Redis: ZREMRANGEBYLEX.</summary>
    RespirePending<long> RemoveRangeByLex(RespireKey key, RespireLexRange range);

    /// <summary>Intersection size, capped by a nonnegative limit (zero means unlimited). Keys must share a Cluster slot. Redis: ZINTERCARD (7.0+).</summary>
    RespirePending<long> IntersectCount(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Intersection size, capped by a nonnegative limit (zero means unlimited). Keys must share a Cluster slot. Redis: ZINTERCARD (7.0+).</summary>
    RespirePending<long> IntersectCount(long limit, params ReadOnlySpan<RespireKey> keys);
}

internal sealed partial class BatchSortedSetCommands
{
    public RespirePending<string?> RandomMember(RespireKey key)
        => sink.Add<Cmd1, string?>("ZRANDMEMBER", new Cmd1(RespireCommands.SortedSet.ZRANDMEMBER.Verb, sink.Client.Key(in key)),
            static (_, reply) => ResponseReader.StringOrNull(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?> RandomMember<T>(RespireKey key)
        => sink.Add<Cmd1, T?>("ZRANDMEMBER", new Cmd1(RespireCommands.SortedSet.ZRANDMEMBER.Verb, sink.Client.Key(in key)),
            static (c, reply) => c.DeserializeBorrowed<T>(in reply));

    public RespirePending<string[]> RandomMembers(RespireKey key, long count)
        => sink.Add<Cmd2, string[]>("ZRANDMEMBER", SortedSetCommands.RandomMembersCommand(sink.Client, key, count),
            static (_, reply) => ResponseReader.StringArray(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T[]> RandomMembers<T>(RespireKey key, long count)
        => sink.Add<Cmd2, T[]>("ZRANDMEMBER", SortedSetCommands.RandomMembersCommand(sink.Client, key, count),
            static (c, reply) => c.DeserializeArray<T>(in reply));

    public RespirePending<SortedSetEntry[]> RandomMembersWithScores(RespireKey key, long count)
        => sink.Add<Cmd3, SortedSetEntry[]>("ZRANDMEMBER", SortedSetCommands.RandomScoredMembersCommand(sink.Client, key, count),
            static (_, reply) => SortedSetCommands.ParseEntries(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<SortedSetEntry<T>[]> RandomMembersWithScores<T>(RespireKey key, long count)
        => sink.Add<Cmd3, SortedSetEntry<T>[]>("ZRANDMEMBER", SortedSetCommands.RandomScoredMembersCommand(sink.Client, key, count),
            static (c, reply) => SortedSetCommands.ParseEntries<T>(c, in reply));

    public RespirePending<long> CountByLex(RespireKey key, RespireLexRange range)
        => sink.Add<Cmd3, long>("ZLEXCOUNT", new Cmd3(RespireCommands.SortedSet.ZLEXCOUNT.Verb,
            sink.Client.Key(in key), range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue()),
            static (_, reply) => ResponseReader.Integer(in reply));

    public RespirePending<long> RemoveRangeByLex(RespireKey key, RespireLexRange range)
        => sink.Add<Cmd3, long>("ZREMRANGEBYLEX", new Cmd3(RespireCommands.SortedSet.ZREMRANGEBYLEX.Verb,
            sink.Client.Key(in key), range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue()),
            static (_, reply) => ResponseReader.Integer(in reply));

    public RespirePending<long> IntersectCount(params ReadOnlySpan<RespireKey> keys)
        => IntersectCount(0, keys);

    public RespirePending<long> IntersectCount(long limit, params ReadOnlySpan<RespireKey> keys)
        => sink.Add<CmdN, long>("ZINTERCARD", SortedSetCommands.IntersectCountCommand(sink.Client, keys, limit), keys,
            static (_, reply) => ResponseReader.Integer(in reply));
}
