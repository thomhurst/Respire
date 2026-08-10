using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// Set (unordered, unique members) commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="ISetCommands"/>.
/// </summary>
public interface IBatchSetCommands
{
    /// <summary>Adds members; returns how many were new. Redis: SADD.</summary>
    RespirePending<long> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Removes members; returns how many existed. Redis: SREM.</summary>
    RespirePending<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Whether the member is in the set. Redis: SISMEMBER.</summary>
    RespirePending<bool> ContainsAsync(RespireKey key, RespireValue member);

    /// <summary>Number of members. Redis: SCARD.</summary>
    RespirePending<long> CountAsync(RespireKey key);

    /// <summary>All members. Redis: SMEMBERS.</summary>
    RespirePending<string[]> MembersAsync(RespireKey key);

    /// <summary>Removes and returns a random member, or null when empty. Redis: SPOP.</summary>
    RespirePending<string?> PopAsync(RespireKey key);

    /// <summary>The intersection of the given sets. Redis: SINTER.</summary>
    RespirePending<string[]> IntersectAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>The union of the given sets. Redis: SUNION.</summary>
    RespirePending<string[]> UnionAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Members of the first set not present in the rest. Redis: SDIFF.</summary>
    RespirePending<string[]> DifferenceAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the intersection into <paramref name="destination"/>; returns its size. Redis: SINTERSTORE.</summary>
    RespirePending<long> IntersectStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the union into <paramref name="destination"/>; returns its size. Redis: SUNIONSTORE.</summary>
    RespirePending<long> UnionStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the difference into <paramref name="destination"/>; returns its size. Redis: SDIFFSTORE.</summary>
    RespirePending<long> DifferenceStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);
}

internal sealed class BatchSetCommands(IPendingSink sink) : IBatchSetCommands
{
    public RespirePending<long> AddAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => sink.Add<Cmd1N, long>(
            "SADD", new Cmd1N(Verbs.SAdd, sink.Client.Key(in key), members.ToArray()),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => sink.Add<Cmd1N, long>(
            "SREM", new Cmd1N(Verbs.SRem, sink.Client.Key(in key), members.ToArray()),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> ContainsAsync(RespireKey key, RespireValue member)
        => sink.Add<Cmd2, bool>(
            "SISMEMBER", new Cmd2(Verbs.SIsMember, sink.Client.Key(in key), member),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> CountAsync(RespireKey key)
        => sink.Add<Cmd1, long>(
            "SCARD", new Cmd1(Verbs.SCard, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string[]> MembersAsync(RespireKey key)
        => sink.Add<Cmd1, string[]>(
            "SMEMBERS", new Cmd1(Verbs.SMembers, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringArray(in v));

    public RespirePending<string?> PopAsync(RespireKey key)
        => sink.Add<Cmd1, string?>(
            "SPOP", new Cmd1(Verbs.SPop, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<string[]> IntersectAsync(params ReadOnlySpan<RespireKey> keys)
        => StringArrayKeys("SINTER", Verbs.SInter, keys);

    public RespirePending<string[]> UnionAsync(params ReadOnlySpan<RespireKey> keys)
        => StringArrayKeys("SUNION", Verbs.SUnion, keys);

    public RespirePending<string[]> DifferenceAsync(params ReadOnlySpan<RespireKey> keys)
        => StringArrayKeys("SDIFF", Verbs.SDiff, keys);

    public RespirePending<long> IntersectStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => StoreKeys("SINTERSTORE", Verbs.SInterStore, destination, keys);

    public RespirePending<long> UnionStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => StoreKeys("SUNIONSTORE", Verbs.SUnionStore, destination, keys);

    public RespirePending<long> DifferenceStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => StoreKeys("SDIFFSTORE", Verbs.SDiffStore, destination, keys);

    private RespirePending<string[]> StringArrayKeys(string operation, Verb verb, ReadOnlySpan<RespireKey> keys)
    {
        return sink.Add<CmdN, string[]>(
            operation, new CmdN(verb, sink.Client.MapKeys(keys)),
            keys,
            static (c, v) => ResponseReader.StringArray(in v));
    }

    private RespirePending<long> StoreKeys(
        string operation, Verb verb, RespireKey destination, ReadOnlySpan<RespireKey> keys)
    {
        return sink.Add<Cmd1N, long>(
            operation, new Cmd1N(verb, sink.Client.Key(in destination), sink.Client.MapKeys(keys)),
            destination, keys,
            static (c, v) => ResponseReader.Integer(in v));
    }
}
