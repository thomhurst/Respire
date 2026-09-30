using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

public partial interface IKeyCommands
{
    /// <summary>Sorts a list, set, or sorted set; missing external GET values are null. Redis: SORT/SORT_RO.</summary>
    ValueTask<string?[]> SortAsync(RespireKey key, RespireSortOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Sorts and deserializes results; use byte[] for owned binary members. Missing GET values are default(T).</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?[]> SortAsync<T>(RespireKey key, RespireSortOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Stores SORT results as a list and returns its length. ReadOnly options are rejected.</summary>
    ValueTask<long> SortStoreAsync(RespireKey key, RespireKey destination, RespireSortOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Returns an owned binary key or null from an empty database. Requires an unprefixed, non-Cluster client.</summary>
    ValueTask<RespireKey?> RandomAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves a key to another database without changing its name; false for a missing source or existing destination.</summary>
    /// <remarks>Requires a standalone server supporting multiple databases. Cluster clients are rejected.</remarks>
    ValueTask<bool> MoveAsync(RespireKey key, int database, CancellationToken cancellationToken = default);
}

internal sealed partial class KeyCommands
{
    public ValueTask<string?[]> SortAsync(RespireKey key, RespireSortOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (operation, command) = SortCommand(client, key, options);
        return client.NullableStringArrayAsync(operation, command, cancellationToken);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?[]> SortAsync<T>(RespireKey key, RespireSortOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (operation, command) = SortCommand(client, key, options);
        return client.DeserializeNullableArrayAsync<T, CmdN>(operation, command, cancellationToken);
    }

    public ValueTask<long> SortStoreAsync(RespireKey key, RespireKey destination, RespireSortOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (operation, command) = SortCommand(client, key, options, destination);
        return client.IntegerAsync(operation, command, cancellationToken);
    }

    public ValueTask<RespireKey?> RandomAsync(CancellationToken cancellationToken = default)
    {
        ValidateRandom(client);
        return client.ConvertResponseAsync("RANDOMKEY", new Cmd(Verbs.RandomKey), cancellationToken, client,
            static (RespireClient _, in RespValue reply) => ParseRandom(in reply));
    }

    public ValueTask<bool> MoveAsync(RespireKey key, int database, CancellationToken cancellationToken = default)
    {
        ValidateMove(client, database);
        return client.FlagAsync("MOVE", new Cmd2(Verbs.Move, client.Key(in key), database), cancellationToken);
    }

    internal static RespireKey? ParseRandom(in RespValue reply)
        => reply.IsNull ? (RespireKey?)null : new RespireKey(reply.AsSpan().ToArray());

    internal static void ValidateRandom(RespireClient client)
    {
        if (!client.KeyPrefixBytes.IsEmpty || client.Core.Cluster is not null)
            throw new NotSupportedException("RANDOMKEY requires an unprefixed, non-Cluster client; it samples the entire selected database.");
    }

    internal static void ValidateMove(RespireClient client, int database)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(database);
        if (client.Core.Cluster is not null)
            throw new NotSupportedException("MOVE is not supported by Cluster clients.");
    }

    internal static (string Operation, CmdN Command) SortCommand(
        RespireClient client, RespireKey key, RespireSortOptions? options, RespireKey? destination = null)
    {
        if (options?.ReadOnly == true && destination.HasValue)
            throw new ArgumentException("SORT_RO does not support STORE.", nameof(options));
        if (options?.Limit is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Offset);
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Count);
        }
        var operation = options?.ReadOnly == true ? "SORT_RO" : "SORT";
        var source = client.Key(in key);
        var arguments = new List<RespireValue> { source };
        if (options?.By is { } by)
        {
            arguments.Add("BY");
            arguments.Add(SortPattern(client, source, by, isGet: false));
        }
        if (options?.Limit is { } slice)
        {
            arguments.Add("LIMIT");
            arguments.Add(slice.Offset);
            arguments.Add(slice.Count);
        }
        if (options is not null)
        {
            foreach (var pattern in options.Get.Span)
            {
                arguments.Add("GET");
                arguments.Add(SortPattern(client, source, pattern, isGet: true));
            }
        }
        arguments.Add(options?.Descending == true ? "DESC" : "ASC");
        if (options?.Alpha == true) arguments.Add("ALPHA");
        if (destination is { } target)
        {
            var resolved = client.Key(in target);
            ValidateSortSlot(client, source, resolved);
            arguments.Add("STORE");
            arguments.Add(resolved);
        }
        return (operation, new CmdN(options?.ReadOnly == true ? Verbs.SortRo : Verbs.Sort, arguments.ToArray()));
    }

    private static RespireValue SortPattern(RespireClient client, RespireValue source, RespireKey pattern, bool isGet)
    {
        var bytes = new byte[pattern.WireLength];
        pattern.AsValue().WriteWirePayload(bytes);
        if ((isGet && bytes.AsSpan().SequenceEqual("#"u8)) || !bytes.Contains((byte)'*'))
            return bytes; // GET # is the member; patterns without * do not access external keys.

        var prefix = client.KeyPrefixBytes;
        if (prefix.Contains((byte)'*') || prefix.IndexOf("->"u8) >= 0 || prefix.Contains((byte)0))
            throw new NotSupportedException("SORT external patterns cannot be used with a key prefix containing *, ->, or NUL.");
        byte[] resolved = [.. prefix, .. bytes];
        if (client.Core.Cluster is not null)
        {
            var keyEnd = resolved.AsSpan().IndexOf("->"u8);
            var keyPattern = keyEnd < 0 ? resolved.AsSpan() : resolved.AsSpan(0, keyEnd);
            var open = keyPattern.IndexOf((byte)'{');
            var close = open < 0 ? -1 : keyPattern[(open + 1)..].IndexOf((byte)'}') + open + 1;
            var wildcard = keyPattern.IndexOf((byte)'*');
            if (open < 0 || close <= open + 1 || wildcard < 0 || close >= wildcard || keyPattern.Contains((byte)0))
                throw new NotSupportedException("Cluster SORT external patterns require a fixed nonempty hash tag before the wildcard (Redis 7.4+).");
            ValidateSortSlot(client, source, new RespireValue(keyPattern.ToArray()));
        }
        return resolved;
    }

    private static void ValidateSortSlot(RespireClient client, RespireValue source, RespireValue other)
    {
        if (client.Core.Cluster is not null && source.AsKey().ClusterSlot != other.AsKey().ClusterSlot)
            throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", "SORT");
    }
}
