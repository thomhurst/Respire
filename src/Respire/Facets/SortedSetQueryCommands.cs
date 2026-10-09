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
    {
        var owner = DispatchResponseSource<string?>.Start();
        try { return owner.Attach(RandomMemberBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?> RandomMemberBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringOrNullAsync("ZRANDMEMBER", new Cmd1(RespireCommands.SortedSet.ZRANDMEMBER.Verb, client.Key(in key)), cancellationToken, observation: observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> RandomMemberAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T?>.Start();
        try { return owner.Attach(RandomMemberBorrowedAsync<T>(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T?> RandomMemberBorrowedAsync<T>(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.DeserializeAsync<T, Cmd1>("ZRANDMEMBER", new Cmd1(RespireCommands.SortedSet.ZRANDMEMBER.Verb, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<string[]> RandomMembersAsync(RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string[]>.Start();
        try { return owner.Attach(RandomMembersBorrowedAsync(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string[]> RandomMembersBorrowedAsync(RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringArrayAsync("ZRANDMEMBER", RandomMembersCommand(client, key, count), cancellationToken, observation: observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RandomMembersAsync<T>(RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T[]>.Start();
        try { return owner.Attach(RandomMembersBorrowedAsync<T>(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T[]> RandomMembersBorrowedAsync<T>(RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.DeserializeArrayAsync<T, Cmd2>("ZRANDMEMBER", RandomMembersCommand(client, key, count), cancellationToken, observation: observation);

    public ValueTask<SortedSetEntry[]> RandomMembersWithScoresAsync(RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<SortedSetEntry[]>.Start();
        try { return owner.Attach(RandomMembersWithScoresBorrowedAsync(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<SortedSetEntry[]> RandomMembersWithScoresBorrowedAsync(RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.ConvertResponseAsync("ZRANDMEMBER", RandomScoredMembersCommand(client, key, count), cancellationToken, this,
            static (SortedSetCommands _, in RespValue reply) => ParseEntries(in reply), observation: observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<SortedSetEntry<T>[]> RandomMembersWithScoresAsync<T>(RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<SortedSetEntry<T>[]>.Start();
        try { return owner.Attach(RandomMembersWithScoresBorrowedAsync<T>(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<SortedSetEntry<T>[]> RandomMembersWithScoresBorrowedAsync<T>(RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.ConvertResponseAsync("ZRANDMEMBER", RandomScoredMembersCommand(client, key, count), cancellationToken, client,
            static (RespireClient c, in RespValue reply) => ParseEntries<T>(c, in reply), observation: observation);

    internal static Cmd2 RandomMembersCommand(RespireClient client, RespireKey key, long count)
    {
        ValidateRandomCount(count);
        return new Cmd2(RespireCommands.SortedSet.ZRANDMEMBER.Verb, client.Key(in key), count);
    }

    internal static Cmd3 RandomScoredMembersCommand(RespireClient client, RespireKey key, long count)
    {
        ValidateRandomCount(count);
        return new Cmd3(RespireCommands.SortedSet.ZRANDMEMBER.Verb, client.Key(in key), count, CommandOptionFrames.WITHSCORESValue);
    }

    private static void ValidateRandomCount(long count)
    {
        if (count == long.MinValue)
            throw new ArgumentOutOfRangeException(nameof(count), "The absolute count must fit in a signed 64-bit integer.");
    }

    public ValueTask<long> CountByLexAsync(RespireKey key, RespireLexRange range, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(CountByLexBorrowedAsync(key, range, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> CountByLexBorrowedAsync(RespireKey key, RespireLexRange range, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("ZLEXCOUNT", new Cmd3(RespireCommands.SortedSet.ZLEXCOUNT.Verb,
            client.Key(in key), range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue()), cancellationToken, observation: observation);

    public ValueTask<long> RemoveRangeByLexAsync(RespireKey key, RespireLexRange range, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(RemoveRangeByLexBorrowedAsync(key, range, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> RemoveRangeByLexBorrowedAsync(RespireKey key, RespireLexRange range, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("ZREMRANGEBYLEX", new Cmd3(RespireCommands.SortedSet.ZREMRANGEBYLEX.Verb,
            client.Key(in key), range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue()), cancellationToken, observation: observation);

    public ValueTask<long> IntersectCountAsync(params ReadOnlySpan<RespireKey> keys)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(IntersectCountBorrowedAsync(keys, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> IntersectCountBorrowedAsync(ReadOnlySpan<RespireKey> keys, RespireTelemetry.ErrorObservation observation)
        => IntersectCountBorrowedAsync(0, keys, CancellationToken.None, observation);

    public ValueTask<long> IntersectCountAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(IntersectCountBorrowedAsync(keys, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> IntersectCountBorrowedAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => IntersectCountBorrowedAsync(0, keys, cancellationToken, observation);

    public ValueTask<long> IntersectCountAsync(long limit, params ReadOnlySpan<RespireKey> keys)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(IntersectCountBorrowedAsync(limit, keys, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> IntersectCountBorrowedAsync(long limit, ReadOnlySpan<RespireKey> keys, RespireTelemetry.ErrorObservation observation)
        => IntersectCountBorrowedAsync(limit, keys, CancellationToken.None, observation);

    public ValueTask<long> IntersectCountAsync(long limit, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(IntersectCountBorrowedAsync(limit, keys, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> IntersectCountBorrowedAsync(long limit, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("ZINTERCARD", IntersectCountCommand(client, keys, limit), cancellationToken, observation: observation);

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
