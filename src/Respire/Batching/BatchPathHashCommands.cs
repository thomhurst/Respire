using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Path Hash commands queued in batches and transactions. Requires Valkey 9.2; tested against RC1.</summary>
public interface IRespireBatchPathHashCommands
{
    /// <summary>Sets one field; false when FNX/FXX rejects the complete write. Valkey: PHSET.</summary>
    RespirePending<bool> Set(RespireKey key, RespireKey path, RespireKey field, RespireValue value, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Atomically sets fields; FNX/FXX tests every field before applying any change. Valkey: PHSET.</summary>
    RespirePending<bool> SetFields(RespireKey key, RespireKey path, ReadOnlySpan<(RespireKey Field, RespireValue Value)> fields, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Atomically sets fields at multiple paths. Valkey: PHMSET.</summary>
    RespirePending<bool> SetMany(RespireKey key, ReadOnlySpan<RespirePathHashWrite> paths);

    /// <summary>Gets owned bytes, or null for a missing path or field. Valkey: PHGET.</summary>
    RespirePending<byte[]?> GetBytes(RespireKey key, RespireKey path, RespireKey field);

    /// <summary>Gets a UTF-8 string, or null for a missing path or field. Valkey: PHGET.</summary>
    RespirePending<string?> GetString(RespireKey key, RespireKey path, RespireKey field);

    /// <summary>Gets owned values in requested field order, retaining missing fields as null. Valkey: PHGET.</summary>
    RespirePending<byte[]?[]> GetFields(RespireKey key, RespireKey path, ReadOnlySpan<RespireKey> fields);

    /// <summary>Gets one owned value in an array, retaining a missing field as null. Valkey: PHGET.</summary>
    RespirePending<byte[]?[]> GetFields(RespireKey key, RespireKey path, RespireKey field);

    /// <summary>Gets a flat array of owned values in requested path-group and field order. Valkey: PHMGET.</summary>
    RespirePending<byte[]?[]> GetMany(RespireKey key, ReadOnlySpan<RespirePathHashRead> paths);

    /// <summary>Gets the owned binary payload; missing paths return an empty array. Valkey: PHGETALL.</summary>
    RespirePending<RespirePathHashField[]> GetAll(RespireKey key, RespireKey path);

    /// <summary>Tests whether an exact logical path exists. Valkey: PHEXISTS.</summary>
    RespirePending<bool> Exists(RespireKey key, RespireKey path);

    /// <summary>Counts logical paths, returning zero when the key is missing. Valkey: PHCARD.</summary>
    RespirePending<long> Count(RespireKey key);

    /// <summary>Deletes specified fields and returns their count; no fields deletes the entire path and returns 0 or 1. Valkey: PHDEL.</summary>
    RespirePending<long> Remove(RespireKey key, RespireKey path, ReadOnlySpan<RespireKey> fields = default);

    /// <summary>Deletes one field and returns its count. Valkey: PHDEL.</summary>
    RespirePending<long> Remove(RespireKey key, RespireKey path, RespireKey field);

    /// <summary>Deletes paths whose bytes start with the prefix; empty prefix removes all paths. Valkey: PHDELPREFIX.</summary>
    RespirePending<long> RemovePrefix(RespireKey key, RespireKey prefix);

    /// <summary>Gets the longest stored byte prefix and its owned payload, or null. Valkey: PHLONGEST.</summary>
    RespirePending<RespirePathHashEntry?> LongestPrefix(RespireKey key, RespireKey query);

    /// <summary>Gets ancestor byte prefixes from shortest to longest; count retains deepest matches after the byte-length limit. Valkey: PHPREFIXES.</summary>
    RespirePending<RespirePathHashEntry[]> Prefixes(RespireKey key, RespireKey query, long? count = null, long? maxPathLength = null);

    /// <summary>Traverses lexicographic paths with an opaque cursor; count is a server hint. An omitted or empty cursor starts traversal; the single ASCII byte "0" ends traversal. Valkey: PHSCAN.</summary>
    RespirePending<RespirePathHashPage> ScanPage(RespireKey key, ReadOnlyMemory<byte> cursor = default, RespireKey? prefix = null, int count = 100);

    /// <summary>Serializes one field and applies a field condition. Valkey: PHSET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [OverloadResolutionPriority(-1)]
    RespirePending<bool> Set<T>(RespireKey key, RespireKey path, RespireKey field, T value, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Deserializes a field; use TryGet to distinguish missing from a stored default. Valkey: PHGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> Get<T>(RespireKey key, RespireKey path, RespireKey field);

    /// <summary>Deserializes a field while preserving presence, including stored defaults. Valkey: PHGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireGetResult<T>> TryGet<T>(RespireKey key, RespireKey path, RespireKey field);
}

internal sealed class BatchPathHashCommands(IPendingSink sink) : IRespireBatchPathHashCommands
{
    public RespirePending<bool> Set(RespireKey key, RespireKey path, RespireKey field, RespireValue value, RespireSetWhen when = RespireSetWhen.Always)
        => sink.Add<Cmd1N, bool>("PHSET",
            Command(RespireCommands.PathHash.PHSET, key, PathHashCodec.Set(path, [(field, value)], when)),
            static (client, reply) => PathHashCodec.Applied(in reply));

    public RespirePending<bool> SetFields(RespireKey key, RespireKey path, ReadOnlySpan<(RespireKey Field, RespireValue Value)> fields, RespireSetWhen when = RespireSetWhen.Always)
        => sink.Add<Cmd1N, bool>("PHSET",
            Command(RespireCommands.PathHash.PHSET, key, PathHashCodec.Set(path, fields, when)),
            static (client, reply) => PathHashCodec.Applied(in reply));

    public RespirePending<bool> SetMany(RespireKey key, ReadOnlySpan<RespirePathHashWrite> paths)
        => sink.Add<Cmd1N, bool>("PHMSET",
            Command(RespireCommands.PathHash.PHMSET, key, PathHashCodec.SetMany(paths)),
            static (client, reply) => ResponseReader.Ok(in reply));

    public RespirePending<byte[]?> GetBytes(RespireKey key, RespireKey path, RespireKey field)
        => sink.Add<Cmd1N, byte[]?>("PHGET",
            Command(RespireCommands.PathHash.PHGET, key, PathHashCodec.Get(path, [field])),
            static (client, reply) => ResponseReader.BytesOrNull(PathHashCodec.Single(in reply)));

    public RespirePending<string?> GetString(RespireKey key, RespireKey path, RespireKey field)
        => sink.Add<Cmd1N, string?>("PHGET",
            Command(RespireCommands.PathHash.PHGET, key, PathHashCodec.Get(path, [field])),
            static (client, reply) => ResponseReader.StringOrNull(PathHashCodec.Single(in reply)));

    public RespirePending<byte[]?[]> GetFields(RespireKey key, RespireKey path, ReadOnlySpan<RespireKey> fields)
        => sink.Add<Cmd1N, byte[]?[]>("PHGET",
            Command(RespireCommands.PathHash.PHGET, key, PathHashCodec.Get(path, fields)),
            static (client, reply) => PathHashCodec.Values(in reply));

    public RespirePending<byte[]?[]> GetFields(RespireKey key, RespireKey path, RespireKey field)
        => GetFields(key, path, [field]);

    public RespirePending<byte[]?[]> GetMany(RespireKey key, ReadOnlySpan<RespirePathHashRead> paths)
        => sink.Add<Cmd1N, byte[]?[]>("PHMGET",
            Command(RespireCommands.PathHash.PHMGET, key, PathHashCodec.GetMany(paths)),
            static (client, reply) => PathHashCodec.Values(in reply));

    public RespirePending<RespirePathHashField[]> GetAll(RespireKey key, RespireKey path)
        => sink.Add<Cmd1N, RespirePathHashField[]>("PHGETALL",
            Command(RespireCommands.PathHash.PHGETALL, key, [path.AsValue()]),
            static (client, reply) => PathHashCodec.Fields(in reply));

    public RespirePending<bool> Exists(RespireKey key, RespireKey path)
        => sink.Add<Cmd1N, bool>("PHEXISTS",
            Command(RespireCommands.PathHash.PHEXISTS, key, [path.AsValue()]),
            static (client, reply) => ResponseReader.Flag(in reply));

    public RespirePending<long> Count(RespireKey key)
        => sink.Add<Cmd1N, long>("PHCARD",
            Command(RespireCommands.PathHash.PHCARD, key, []),
            static (client, reply) => ResponseReader.Integer(in reply));

    public RespirePending<long> Remove(RespireKey key, RespireKey path, ReadOnlySpan<RespireKey> fields = default)
        => sink.Add<Cmd1N, long>("PHDEL",
            Command(RespireCommands.PathHash.PHDEL, key, PathHashCodec.Get(path, fields, allowEmpty: true)),
            static (client, reply) => ResponseReader.Integer(in reply));

    public RespirePending<long> Remove(RespireKey key, RespireKey path, RespireKey field)
        => Remove(key, path, [field]);

    public RespirePending<long> RemovePrefix(RespireKey key, RespireKey prefix)
        => sink.Add<Cmd1N, long>("PHDELPREFIX",
            Command(RespireCommands.PathHash.PHDELPREFIX, key, [prefix.AsValue()]),
            static (client, reply) => ResponseReader.Integer(in reply));

    public RespirePending<RespirePathHashEntry?> LongestPrefix(RespireKey key, RespireKey query)
        => sink.Add<Cmd1N, RespirePathHashEntry?>("PHLONGEST",
            Command(RespireCommands.PathHash.PHLONGEST, key, [query.AsValue(), "WITHVALUES"]),
            static (client, reply) => PathHashCodec.Match(in reply));

    public RespirePending<RespirePathHashEntry[]> Prefixes(RespireKey key, RespireKey query, long? count = null, long? maxPathLength = null)
        => sink.Add<Cmd1N, RespirePathHashEntry[]>("PHPREFIXES",
            Command(RespireCommands.PathHash.PHPREFIXES, key, PathHashCodec.Prefixes(query, count, maxPathLength)),
            static (client, reply) => PathHashCodec.Entries(in reply));

    public RespirePending<RespirePathHashPage> ScanPage(RespireKey key, ReadOnlyMemory<byte> cursor = default, RespireKey? prefix = null, int count = 100)
        => sink.Add<Cmd1N, RespirePathHashPage>("PHSCAN",
            Command(RespireCommands.PathHash.PHSCAN, key, PathHashCodec.Scan(cursor, prefix, count)),
            static (client, reply) => PathHashCodec.Page(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [OverloadResolutionPriority(-1)]
    public RespirePending<bool> Set<T>(RespireKey key, RespireKey path, RespireKey field, T value, RespireSetWhen when = RespireSetWhen.Always)
        => sink.Add<Cmd1N, bool>("PHSET",
            Command(RespireCommands.PathHash.PHSET, key, PathHashCodec.Set(path, [(field, sink.Client.SerializeRawCompatible(value))], when)),
            static (client, reply) => PathHashCodec.Applied(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?> Get<T>(RespireKey key, RespireKey path, RespireKey field)
        => sink.Add<Cmd1N, T?>("PHGET",
            Command(RespireCommands.PathHash.PHGET, key, PathHashCodec.Get(path, [field])),
            static (client, reply) => client.DeserializeBorrowed<T>(PathHashCodec.Single(in reply)));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<RespireGetResult<T>> TryGet<T>(RespireKey key, RespireKey path, RespireKey field)
        => sink.Add<Cmd1N, RespireGetResult<T>>("PHGET",
            Command(RespireCommands.PathHash.PHGET, key, PathHashCodec.Get(path, [field])),
            static (client, reply) => client.TryDeserializeBorrowed<T>(PathHashCodec.Single(in reply)));

    private Cmd1N Command(RespireCommand command, RespireKey key, RespireValue[] args)
    {
        for (var i = 0; i < args.Length; i++) args[i] = args[i].Snapshot();
        return new(command.Verb, sink.Client.Key(in key).Snapshot(), args);
    }
}
