using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Valkey 9.2 Path Hash commands. Tested against 9.2.0-rc1; GA compatibility is unverified. Paths and fields are binary, and only the containing key receives a key prefix.</summary>
public interface IRespirePathHashCommands
{
    /// <summary>Sets one field; false when FNX/FXX rejects the complete write. Valkey: PHSET.</summary>
    ValueTask<bool> SetAsync(RespireKey key, RespireKey path, RespireKey field, RespireValue value, RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);

    /// <summary>Atomically sets fields; FNX/FXX tests every field before applying any change. Valkey: PHSET.</summary>
    ValueTask<bool> SetFieldsAsync(RespireKey key, RespireKey path, ReadOnlySpan<(RespireKey Field, RespireValue Value)> fields, RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);

    /// <summary>Atomically sets fields at multiple paths. Valkey: PHMSET.</summary>
    ValueTask<bool> SetManyAsync(RespireKey key, ReadOnlySpan<RespirePathHashWrite> paths, CancellationToken cancellationToken = default);

    /// <summary>Gets owned bytes, or null for a missing path or field. Valkey: PHGET.</summary>
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default);

    /// <summary>Gets a UTF-8 string, or null for a missing path or field. Valkey: PHGET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default);

    /// <summary>Gets owned values in requested field order, retaining missing fields as null. Valkey: PHGET.</summary>
    ValueTask<byte[]?[]> GetFieldsAsync(RespireKey key, RespireKey path, ReadOnlySpan<RespireKey> fields, CancellationToken cancellationToken = default);

    /// <summary>Gets one owned value in an array, retaining a missing field as null. Valkey: PHGET.</summary>
    ValueTask<byte[]?[]> GetFieldsAsync(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default);

    /// <summary>Gets a flat array of owned values in requested path-group and field order. Valkey: PHMGET.</summary>
    ValueTask<byte[]?[]> GetManyAsync(RespireKey key, ReadOnlySpan<RespirePathHashRead> paths, CancellationToken cancellationToken = default);

    /// <summary>Gets the owned binary payload; missing paths return an empty array. Valkey: PHGETALL.</summary>
    ValueTask<RespirePathHashField[]> GetAllAsync(RespireKey key, RespireKey path, CancellationToken cancellationToken = default);

    /// <summary>Tests whether an exact logical path exists. Valkey: PHEXISTS.</summary>
    ValueTask<bool> ExistsAsync(RespireKey key, RespireKey path, CancellationToken cancellationToken = default);

    /// <summary>Counts logical paths, returning zero when the key is missing. Valkey: PHCARD.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Deletes specified fields and returns their count; no fields deletes the entire path and returns 0 or 1. Valkey: PHDEL.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, RespireKey path, ReadOnlySpan<RespireKey> fields = default, CancellationToken cancellationToken = default);

    /// <summary>Deletes one field and returns its count. Valkey: PHDEL.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default);

    /// <summary>Deletes paths whose bytes start with the prefix; empty prefix removes all paths. Valkey: PHDELPREFIX.</summary>
    ValueTask<long> RemovePrefixAsync(RespireKey key, RespireKey prefix, CancellationToken cancellationToken = default);

    /// <summary>Gets the longest stored byte prefix and its owned payload, or null. Valkey: PHLONGEST.</summary>
    ValueTask<RespirePathHashEntry?> LongestPrefixAsync(RespireKey key, RespireKey query, CancellationToken cancellationToken = default);

    /// <summary>Gets ancestor byte prefixes from shortest to longest; count retains deepest matches after the byte-length limit. Valkey: PHPREFIXES.</summary>
    ValueTask<RespirePathHashEntry[]> PrefixesAsync(RespireKey key, RespireKey query, long? count = null, long? maxPathLength = null, CancellationToken cancellationToken = default);

    /// <summary>Traverses lexicographic paths with an opaque cursor; count is a server hint, and cursor 0 ends traversal. Manual paging does not guarantee replica affinity; use ScanAsync to pin an enumeration. Valkey: PHSCAN.</summary>
    ValueTask<RespirePathHashPage> ScanPageAsync(RespireKey key, string cursor = "0", RespireKey? prefix = null, int count = 100, CancellationToken cancellationToken = default);

    /// <summary>Serializes one field and applies a field condition. Valkey: PHSET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [OverloadResolutionPriority(-1)]
    ValueTask<bool> SetAsync<T>(RespireKey key, RespireKey path, RespireKey field, T value, RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);

    /// <summary>Deserializes a field; use TryGet to distinguish missing from a stored default. Valkey: PHGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAsync<T>(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default);

    /// <summary>Deserializes a field while preserving presence, including stored defaults. Valkey: PHGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireGetResult<T>> TryGetAsync<T>(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default);

    /// <summary>Streams lexicographic descendants of a byte prefix. Pages are separate reads, not a snapshot; concurrent changes may alter traversal.</summary>
    IAsyncEnumerable<RespirePathHashEntry> ScanAsync(RespireKey key, RespireKey? prefix = null, int count = 100, CancellationToken cancellationToken = default);
}

internal sealed class PathHashCommands(RespireClient client) : IRespirePathHashCommands
{
    public ValueTask<byte[]?[]> GetFieldsAsync(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default)
        => GetFieldsAsync(key, path, [field], cancellationToken);

    public ValueTask<long> RemoveAsync(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default)
        => RemoveAsync(key, path, [field], cancellationToken);

    public ValueTask<bool> SetAsync(RespireKey key, RespireKey path, RespireKey field, RespireValue value, RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<bool>(cancellationToken));
            var args = PathHashCodec.Set(path, [(field, value)], when);
            return owner.Attach(client.ConvertResponseAsync("PHSET",
                new Cmd1N(RespireCommands.PathHash.PHSET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Applied(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<bool> SetFieldsAsync(RespireKey key, RespireKey path, ReadOnlySpan<(RespireKey Field, RespireValue Value)> fields, RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<bool>(cancellationToken));
            var args = PathHashCodec.Set(path, fields, when);
            return owner.Attach(client.ConvertResponseAsync("PHSET",
                new Cmd1N(RespireCommands.PathHash.PHSET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Applied(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<bool> SetManyAsync(RespireKey key, ReadOnlySpan<RespirePathHashWrite> paths, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<bool>(cancellationToken));
            var args = PathHashCodec.SetMany(paths);
            return owner.Attach(client.ConvertResponseAsync("PHMSET",
                new Cmd1N(RespireCommands.PathHash.PHMSET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => ResponseReader.Ok(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<byte[]?>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<byte[]?>(cancellationToken));
            var args = PathHashCodec.Get(path, [field]);
            return owner.Attach(client.ConvertResponseAsync("PHGET",
                new Cmd1N(RespireCommands.PathHash.PHGET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => ResponseReader.BytesOrNull(PathHashCodec.Single(in reply)), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<string?> GetStringAsync(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string?>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<string?>(cancellationToken));
            var args = PathHashCodec.Get(path, [field]);
            return owner.Attach(client.ConvertResponseAsync("PHGET",
                new Cmd1N(RespireCommands.PathHash.PHGET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => ResponseReader.StringOrNull(PathHashCodec.Single(in reply)), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<byte[]?[]> GetFieldsAsync(RespireKey key, RespireKey path, ReadOnlySpan<RespireKey> fields, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<byte[]?[]>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<byte[]?[]>(cancellationToken));
            var args = PathHashCodec.Get(path, fields);
            return owner.Attach(client.ConvertResponseAsync("PHGET",
                new Cmd1N(RespireCommands.PathHash.PHGET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Values(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<byte[]?[]> GetManyAsync(RespireKey key, ReadOnlySpan<RespirePathHashRead> paths, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<byte[]?[]>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<byte[]?[]>(cancellationToken));
            var args = PathHashCodec.GetMany(paths);
            return owner.Attach(client.ConvertResponseAsync("PHMGET",
                new Cmd1N(RespireCommands.PathHash.PHMGET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Values(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<RespirePathHashField[]> GetAllAsync(RespireKey key, RespireKey path, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespirePathHashField[]>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<RespirePathHashField[]>(cancellationToken));
            RespireValue[] args = [path.AsValue()];
            return owner.Attach(client.ConvertResponseAsync("PHGETALL",
                new Cmd1N(RespireCommands.PathHash.PHGETALL.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Fields(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<bool> ExistsAsync(RespireKey key, RespireKey path, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<bool>(cancellationToken));
            RespireValue[] args = [path.AsValue()];
            return owner.Attach(client.ConvertResponseAsync("PHEXISTS",
                new Cmd1N(RespireCommands.PathHash.PHEXISTS.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => ResponseReader.Flag(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<long>(cancellationToken));
            var args = Array.Empty<RespireValue>();
            return owner.Attach(client.ConvertResponseAsync("PHCARD",
                new Cmd1N(RespireCommands.PathHash.PHCARD.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => ResponseReader.Integer(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<long> RemoveAsync(RespireKey key, RespireKey path, ReadOnlySpan<RespireKey> fields = default, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<long>(cancellationToken));
            var args = PathHashCodec.Get(path, fields, allowEmpty: true);
            return owner.Attach(client.ConvertResponseAsync("PHDEL",
                new Cmd1N(RespireCommands.PathHash.PHDEL.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => ResponseReader.Integer(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<long> RemovePrefixAsync(RespireKey key, RespireKey prefix, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<long>(cancellationToken));
            RespireValue[] args = [prefix.AsValue()];
            return owner.Attach(client.ConvertResponseAsync("PHDELPREFIX",
                new Cmd1N(RespireCommands.PathHash.PHDELPREFIX.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => ResponseReader.Integer(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<RespirePathHashEntry?> LongestPrefixAsync(RespireKey key, RespireKey query, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespirePathHashEntry?>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<RespirePathHashEntry?>(cancellationToken));
            RespireValue[] args = [query.AsValue(), "WITHVALUES"];
            return owner.Attach(client.ConvertResponseAsync("PHLONGEST",
                new Cmd1N(RespireCommands.PathHash.PHLONGEST.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Match(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<RespirePathHashEntry[]> PrefixesAsync(RespireKey key, RespireKey query, long? count = null, long? maxPathLength = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespirePathHashEntry[]>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<RespirePathHashEntry[]>(cancellationToken));
            var args = PathHashCodec.Prefixes(query, count, maxPathLength);
            return owner.Attach(client.ConvertResponseAsync("PHPREFIXES",
                new Cmd1N(RespireCommands.PathHash.PHPREFIXES.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Entries(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<RespirePathHashPage> ScanPageAsync(RespireKey key, string cursor = "0", RespireKey? prefix = null, int count = 100, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespirePathHashPage>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<RespirePathHashPage>(cancellationToken));
            var args = PathHashCodec.Scan(cursor, prefix, count);
            return owner.Attach(client.ConvertResponseAsync("PHSCAN",
                new Cmd1N(RespireCommands.PathHash.PHSCAN.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Page(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> SetAsync<T>(RespireKey key, RespireKey path, RespireKey field, T value, RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<bool>(cancellationToken));
            var args = PathHashCodec.Set(path, [(field, client.SerializeRawCompatible(value))], when);
            return owner.Attach(client.ConvertResponseAsync("PHSET",
                new Cmd1N(RespireCommands.PathHash.PHSET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => PathHashCodec.Applied(in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAsync<T>(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T?>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<T?>(cancellationToken));
            var args = PathHashCodec.Get(path, [field]);
            return owner.Attach(client.ConvertResponseAsync("PHGET",
                new Cmd1N(RespireCommands.PathHash.PHGET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => client.DeserializeBorrowed<T>(PathHashCodec.Single(in reply)), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireGetResult<T>> TryGetAsync<T>(RespireKey key, RespireKey path, RespireKey field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireGetResult<T>>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<RespireGetResult<T>>(cancellationToken));
            var args = PathHashCodec.Get(path, [field]);
            return owner.Attach(client.ConvertResponseAsync("PHGET",
                new Cmd1N(RespireCommands.PathHash.PHGET.Verb, client.Key(in key), args), cancellationToken,
                client, static (RespireClient client, in RespValue reply) => client.TryDeserializeBorrowed<T>(PathHashCodec.Single(in reply)), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public async IAsyncEnumerable<RespirePathHashEntry> ScanAsync(RespireKey key, RespireKey? prefix = null, int count = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cursor = "0";
        var affinity = new ReadAffinity();
        do
        {
            var page = await ReadScanPageAsync(key, cursor, prefix, count, affinity, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Entries) { CollectionScan.CheckCancellation(cancellationToken); yield return entry; }
            cursor = page.Cursor;
        } while (cursor != "0");
    }

    private ValueTask<RespirePathHashPage> ReadScanPageAsync(RespireKey key, string cursor, RespireKey? prefix, int count,
        ReadAffinity affinity, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<RespirePathHashPage>.Start();
        try
        {
            if (client.Core.Options.CommandHook is null && cancellationToken.IsCancellationRequested)
                return owner.Attach(ValueTask.FromCanceled<RespirePathHashPage>(cancellationToken));
            var args = PathHashCodec.Scan(cursor, prefix, count);
            return owner.Attach(client.ConvertCursorPageAsync("PHSCAN",
                new Cmd1N(RespireCommands.PathHash.PHSCAN.Verb, client.Key(in key), args), affinity, cancellationToken,
                client, static (RespireClient _, in RespValue reply) => PathHashCodec.Page(in reply), owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }
}
