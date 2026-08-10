using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// Set (unordered, unique members) commands. Collection cardinality uses
/// <see cref="CountAsync"/>.
/// </summary>
public interface ISetCommands
{
    /// <summary>Adds members; returns how many were new. Redis: SADD.</summary>
    ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Adds members; returns how many were new. Redis: SADD.</summary>
    ValueTask<long> AddAsync(RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken);

    /// <summary>Removes members; returns how many existed. Redis: SREM.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Removes members; returns how many existed. Redis: SREM.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken);

    /// <summary>Whether the member is in the set. Redis: SISMEMBER.</summary>
    ValueTask<bool> ContainsAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a serialized <typeparamref name="T"/> is in the set. Redis: SISMEMBER.
    /// <para>
    /// An argument already typed as <see cref="RespireValue"/> picks the non-generic overload;
    /// any other type picks this one. Boolean members retain the Redis-native <c>1</c>/<c>0</c>
    /// encoding used by <see cref="AddAsync(RespireKey, ReadOnlySpan{RespireValue})"/>; other types
    /// use normal typed serialization.
    /// </para>
    /// </summary>
    ValueTask<bool> ContainsAsync<T>(RespireKey key, T member, CancellationToken cancellationToken = default);

    /// <summary>Number of members. Redis: SCARD.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>All members. Redis: SMEMBERS.</summary>
    ValueTask<string[]> MembersAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Iterates members incrementally. Redis: SSCAN.</summary>
    IAsyncEnumerable<string> ScanAsync(
        RespireKey key, string? match = null, int countHint = 250,
        CancellationToken cancellationToken = default);

    /// <summary>Removes and returns a random member, or null when empty. Redis: SPOP.</summary>
    ValueTask<string?> PopAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Removes and returns up to <paramref name="count"/> random members. Redis: SPOP.</summary>
    ValueTask<string[]> PopAsync(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns random members without removing them. Negative counts allow duplicates. Redis: SRANDMEMBER.
    /// </summary>
    ValueTask<string[]> RandomMembersAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>The intersection of the given sets. Redis: SINTER.</summary>
    ValueTask<string[]> IntersectAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>The intersection of the given sets. Redis: SINTER.</summary>
    ValueTask<string[]> IntersectAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>The union of the given sets. Redis: SUNION.</summary>
    ValueTask<string[]> UnionAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>The union of the given sets. Redis: SUNION.</summary>
    ValueTask<string[]> UnionAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Members of the first set not present in the rest. Redis: SDIFF.</summary>
    ValueTask<string[]> DifferenceAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Members of the first set not present in the rest. Redis: SDIFF.</summary>
    ValueTask<string[]> DifferenceAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Stores the intersection into <paramref name="destination"/>; returns its size. Redis: SINTERSTORE.</summary>
    ValueTask<long> IntersectStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the intersection into <paramref name="destination"/>; returns its size. Redis: SINTERSTORE.</summary>
    ValueTask<long> IntersectStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Stores the union into <paramref name="destination"/>; returns its size. Redis: SUNIONSTORE.</summary>
    ValueTask<long> UnionStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the union into <paramref name="destination"/>; returns its size. Redis: SUNIONSTORE.</summary>
    ValueTask<long> UnionStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Stores the difference into <paramref name="destination"/>; returns its size. Redis: SDIFFSTORE.</summary>
    ValueTask<long> DifferenceStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the difference into <paramref name="destination"/>; returns its size. Redis: SDIFFSTORE.</summary>
    ValueTask<long> DifferenceStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);
}

internal sealed class SetCommands(RespireClient client) : ISetCommands
{
    public ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => AddAsync(key, members, CancellationToken.None);

    public ValueTask<long> AddAsync(RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken)
        => client.IntegerValuesAsync("SADD", Verbs.SAdd, client.Key(in key), members, cancellationToken);

    public ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => RemoveAsync(key, members, CancellationToken.None);

    public ValueTask<long> RemoveAsync(RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken)
        => client.IntegerValuesAsync("SREM", Verbs.SRem, client.Key(in key), members, cancellationToken);

    public ValueTask<bool> ContainsAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => client.FlagAsync("SISMEMBER", new Cmd2(Verbs.SIsMember, client.Key(in key), member), cancellationToken);

    public ValueTask<bool> ContainsAsync<T>(RespireKey key, T member, CancellationToken cancellationToken = default)
        => client.FlagAsync(
            "SISMEMBER",
            new Cmd2(Verbs.SIsMember, client.Key(in key), client.SerializeCollectionMember(member)),
            cancellationToken);

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("SCARD", new Cmd1(Verbs.SCard, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> MembersAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("SMEMBERS", new Cmd1(Verbs.SMembers, client.Key(in key)), cancellationToken);

    public IAsyncEnumerable<string> ScanAsync(
        RespireKey key, string? match = null, int countHint = 250,
        CancellationToken cancellationToken = default)
        => CollectionScan.EnumerateAsync(
            client, "SSCAN", RespireCommands.Set.SSCAN.Verb, key, match, countHint,
            ParseScanMembers, cancellationToken);

    public ValueTask<string?> PopAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("SPOP", new Cmd1(Verbs.SPop, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> PopAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return client.StringArrayAsync(
            "SPOP", new Cmd2(Verbs.SPop, client.Key(in key), count), cancellationToken);
    }

    public ValueTask<string[]> RandomMembersAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
        => client.StringArrayAsync(
            "SRANDMEMBER", new Cmd2(Verbs.SRandMember, client.Key(in key), count), cancellationToken);

    private static string[] ParseScanMembers(in RespValue page) => ResponseReader.StringArray(in page);

    public ValueTask<string[]> IntersectAsync(params ReadOnlySpan<RespireKey> keys)
        => IntersectAsync(keys, CancellationToken.None);

    public ValueTask<string[]> IntersectAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.StringArrayAsync("SINTER", new CmdN(Verbs.SInter, client.MapKeys(keys)), cancellationToken);

    public ValueTask<string[]> UnionAsync(params ReadOnlySpan<RespireKey> keys)
        => UnionAsync(keys, CancellationToken.None);

    public ValueTask<string[]> UnionAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.StringArrayAsync("SUNION", new CmdN(Verbs.SUnion, client.MapKeys(keys)), cancellationToken);

    public ValueTask<string[]> DifferenceAsync(params ReadOnlySpan<RespireKey> keys)
        => DifferenceAsync(keys, CancellationToken.None);

    public ValueTask<string[]> DifferenceAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.StringArrayAsync("SDIFF", new CmdN(Verbs.SDiff, client.MapKeys(keys)), cancellationToken);

    public ValueTask<long> IntersectStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => IntersectStoreAsync(destination, keys, CancellationToken.None);

    public ValueTask<long> IntersectStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.IntegerAsync(
            "SINTERSTORE", new Cmd1N(Verbs.SInterStore, client.Key(in destination), client.MapKeys(keys)), cancellationToken);

    public ValueTask<long> UnionStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => UnionStoreAsync(destination, keys, CancellationToken.None);

    public ValueTask<long> UnionStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.IntegerAsync(
            "SUNIONSTORE", new Cmd1N(Verbs.SUnionStore, client.Key(in destination), client.MapKeys(keys)), cancellationToken);

    public ValueTask<long> DifferenceStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => DifferenceStoreAsync(destination, keys, CancellationToken.None);

    public ValueTask<long> DifferenceStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.IntegerAsync(
            "SDIFFSTORE", new Cmd1N(Verbs.SDiffStore, client.Key(in destination), client.MapKeys(keys)), cancellationToken);
}
