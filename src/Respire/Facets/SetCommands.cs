using Respire.Commands;

namespace Respire;

/// <summary>Set (unordered, unique members) commands.</summary>
public interface ISetCommands
{
    /// <summary>Adds members; returns how many were new. Redis: SADD.</summary>
    ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Removes members; returns how many existed. Redis: SREM.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Whether the member is in the set. Redis: SISMEMBER.</summary>
    ValueTask<bool> ContainsAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);

    /// <summary>Number of members. Redis: SCARD.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>All members. Redis: SMEMBERS.</summary>
    ValueTask<string[]> MembersAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Removes and returns a random member, or null when empty. Redis: SPOP.</summary>
    ValueTask<string?> PopAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>The intersection of the given sets. Redis: SINTER.</summary>
    ValueTask<string[]> IntersectAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>The union of the given sets. Redis: SUNION.</summary>
    ValueTask<string[]> UnionAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Members of the first set not present in the rest. Redis: SDIFF.</summary>
    ValueTask<string[]> DifferenceAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the intersection into <paramref name="destination"/>; returns its size. Redis: SINTERSTORE.</summary>
    ValueTask<long> IntersectStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the union into <paramref name="destination"/>; returns its size. Redis: SUNIONSTORE.</summary>
    ValueTask<long> UnionStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the difference into <paramref name="destination"/>; returns its size. Redis: SDIFFSTORE.</summary>
    ValueTask<long> DifferenceStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);
}

internal sealed class SetCommands(RespireClient client) : ISetCommands
{
    public ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => client.IntegerValuesAsync("SADD", Verbs.SAdd, client.Key(in key), members);

    public ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => client.IntegerValuesAsync("SREM", Verbs.SRem, client.Key(in key), members);

    public ValueTask<bool> ContainsAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => client.FlagAsync("SISMEMBER", new Cmd2(Verbs.SIsMember, client.Key(in key), member), cancellationToken);

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("SCARD", new Cmd1(Verbs.SCard, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> MembersAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("SMEMBERS", new Cmd1(Verbs.SMembers, client.Key(in key)), cancellationToken);

    public ValueTask<string?> PopAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("SPOP", new Cmd1(Verbs.SPop, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> IntersectAsync(params ReadOnlySpan<RespireKey> keys)
        => client.StringArrayAsync("SINTER", new CmdN(Verbs.SInter, client.MapKeys(keys)), CancellationToken.None);

    public ValueTask<string[]> UnionAsync(params ReadOnlySpan<RespireKey> keys)
        => client.StringArrayAsync("SUNION", new CmdN(Verbs.SUnion, client.MapKeys(keys)), CancellationToken.None);

    public ValueTask<string[]> DifferenceAsync(params ReadOnlySpan<RespireKey> keys)
        => client.StringArrayAsync("SDIFF", new CmdN(Verbs.SDiff, client.MapKeys(keys)), CancellationToken.None);

    public ValueTask<long> IntersectStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => client.IntegerAsync(
            "SINTERSTORE", new Cmd1N(Verbs.SInterStore, client.Key(in destination), client.MapKeys(keys)), CancellationToken.None);

    public ValueTask<long> UnionStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => client.IntegerAsync(
            "SUNIONSTORE", new Cmd1N(Verbs.SUnionStore, client.Key(in destination), client.MapKeys(keys)), CancellationToken.None);

    public ValueTask<long> DifferenceStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => client.IntegerAsync(
            "SDIFFSTORE", new Cmd1N(Verbs.SDiffStore, client.Key(in destination), client.MapKeys(keys)), CancellationToken.None);
}
