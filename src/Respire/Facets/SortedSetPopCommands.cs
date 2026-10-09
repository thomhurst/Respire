using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

public partial interface ISortedSetCommands
{
    /// <summary>Pops up to count entries from the first nonempty sorted set. Redis: ZMPOP / BZMPOP (7.0+).</summary>
    /// <remarks>Count must be positive. Keys must share a Cluster slot after prefixing. Omit waitFor for nonblocking execution.
    /// Timeout.InfiniteTimeSpan waits indefinitely; TimeSpan.Zero uses a one-millisecond blocking wait. Null means no entry was available.</remarks>
    ValueTask<RespireSortedSetPopManyResult?> PopManyAsync(
        ReadOnlySpan<RespireKey> keys, long count = 1, bool descending = false,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Waits for one entry from the first nonempty sorted set, using a dedicated connection. Redis: BZPOPMIN / BZPOPMAX.</summary>
    /// <remarks>Keys must share a Cluster slot. Timeout.InfiniteTimeSpan waits indefinitely; zero uses one millisecond. Null means timeout.</remarks>
    ValueTask<RespireSortedSetPopResult?> PopAsync(
        ReadOnlySpan<RespireKey> keys, TimeSpan waitFor, bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>Pops up to count entries from the first nonempty sorted set. Redis: ZMPOP / BZMPOP (7.0+).</summary>
    /// <remarks>Count must be positive. Keys must share a Cluster slot after prefixing. Omit waitFor for nonblocking execution.
    /// Timeout.InfiniteTimeSpan waits indefinitely; TimeSpan.Zero uses a one-millisecond blocking wait. Null means no entry was available.</remarks>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireSortedSetPopManyResult<T>?> PopManyAsync<T>(
        ReadOnlySpan<RespireKey> keys, long count = 1, bool descending = false,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Waits for one entry from the first nonempty sorted set, using a dedicated connection. Redis: BZPOPMIN / BZPOPMAX.</summary>
    /// <remarks>Keys must share a Cluster slot. Timeout.InfiniteTimeSpan waits indefinitely; zero uses one millisecond. Null means timeout.</remarks>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireSortedSetPopResult<T>?> PopAsync<T>(
        ReadOnlySpan<RespireKey> keys, TimeSpan waitFor, bool descending = false,
        CancellationToken cancellationToken = default);

}

internal sealed partial class SortedSetCommands
{
    public ValueTask<RespireSortedSetPopManyResult?> PopManyAsync(
        ReadOnlySpan<RespireKey> keys, long count = 1, bool descending = false,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireSortedSetPopManyResult?>.Start();
        try
        {
            var (operation, command) = PopManyCommand(client, keys, count, descending, waitFor);
            return owner.Attach(waitFor.HasValue
                ? PopManyBlockingAsync(operation, command, cancellationToken, owner.Observation)
                : client.ConvertResponseAsync(operation, command, cancellationToken, client,
                    static (RespireClient c, in RespValue reply) => ParsePopMany(in reply, c.KeyPrefixBytes), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    public ValueTask<RespireSortedSetPopResult?> PopAsync(
        ReadOnlySpan<RespireKey> keys, TimeSpan waitFor, bool descending = false,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireSortedSetPopResult?>.Start();
        try
        {
            var (operation, command) = PopOneCommand(client, keys, waitFor, descending);
            return owner.Attach(PopOneBlockingAsync(operation, command, cancellationToken, owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireSortedSetPopManyResult?> PopManyBlockingAsync(
        string operation, CmdN command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        return await client.ConvertBlockingResponseAsync(operation, command, cancellationToken, client,
            static (RespireClient owner, in RespValue reply) => ParsePopMany(in reply, owner.KeyPrefixBytes), observation)
            .ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireSortedSetPopResult?> PopOneBlockingAsync(
        string operation, CmdN command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        return await client.ConvertBlockingResponseAsync(operation, command, cancellationToken, client,
            static (RespireClient owner, in RespValue reply) => ParsePopOne(in reply, owner.KeyPrefixBytes), observation)
            .ConfigureAwait(false);
    }

    internal static RespireSortedSetPopManyResult? ParsePopMany(in RespValue reply, ReadOnlySpan<byte> prefix)
    {
        if (reply.IsNull) return null;
        var elements = PopReplyElements(in reply, 2);
        ValidatePopEntries(in elements[1]);
        return new RespireSortedSetPopManyResult(MultiKeyPop.ParsePoppedKey(in elements[0], prefix), ParseEntries(in elements[1]));
    }

    internal static RespireSortedSetPopResult? ParsePopOne(in RespValue reply, ReadOnlySpan<byte> prefix)
    {
        if (reply.IsNull) return null;
        var elements = PopReplyElements(in reply, 3);
        return new RespireSortedSetPopResult(MultiKeyPop.ParsePoppedKey(in elements[0], prefix),
            new SortedSetEntry(elements[1].AsString(), ResponseReader.Double(in elements[2])));
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireSortedSetPopManyResult<T>?> PopManyAsync<T>(
        ReadOnlySpan<RespireKey> keys, long count = 1, bool descending = false,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireSortedSetPopManyResult<T>?>.Start();
        try
        {
            var (operation, command) = PopManyCommand(client, keys, count, descending, waitFor);
            return owner.Attach(waitFor.HasValue
                ? PopManyBlockingAsync<T>(operation, command, cancellationToken, owner.Observation)
                : client.ConvertResponseAsync(operation, command, cancellationToken, client,
                    static (RespireClient c, in RespValue reply) => ParsePopMany<T>(c, in reply), observation: owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireSortedSetPopResult<T>?> PopAsync<T>(
        ReadOnlySpan<RespireKey> keys, TimeSpan waitFor, bool descending = false,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireSortedSetPopResult<T>?>.Start();
        try
        {
            var (operation, command) = PopOneCommand(client, keys, waitFor, descending);
            return owner.Attach(PopOneBlockingAsync<T>(operation, command, cancellationToken, owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireSortedSetPopManyResult<T>?> PopManyBlockingAsync<T>(
        string operation, CmdN command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        return await client.ConvertBlockingResponseAsync(operation, command, cancellationToken, client,
            static (RespireClient owner, in RespValue reply) => ParsePopMany<T>(owner, in reply), observation)
            .ConfigureAwait(false);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireSortedSetPopResult<T>?> PopOneBlockingAsync<T>(
        string operation, CmdN command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        return await client.ConvertBlockingResponseAsync(operation, command, cancellationToken, client,
            static (RespireClient owner, in RespValue reply) => ParsePopOne<T>(owner, in reply), observation)
            .ConfigureAwait(false);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal static RespireSortedSetPopManyResult<T>? ParsePopMany<T>(RespireClient client, in RespValue reply)
    {
        if (reply.IsNull) return null;
        var elements = PopReplyElements(in reply, 2);
        ValidatePopEntries(in elements[1]);
        return new RespireSortedSetPopManyResult<T>(MultiKeyPop.ParsePoppedKey(in elements[0], client.KeyPrefixBytes), ParseEntries<T>(client, in elements[1]));
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal static RespireSortedSetPopResult<T>? ParsePopOne<T>(RespireClient client, in RespValue reply)
    {
        if (reply.IsNull) return null;
        var elements = PopReplyElements(in reply, 3);
        return new RespireSortedSetPopResult<T>(MultiKeyPop.ParsePoppedKey(in elements[0], client.KeyPrefixBytes),
            new SortedSetEntry<T>(client.DeserializeBorrowed<T>(in elements[1])!, ResponseReader.Double(in elements[2])));
    }

    internal static (string Operation, CmdN Command) PopManyCommand(
        RespireClient client, ReadOnlySpan<RespireKey> keys, long count, bool descending, TimeSpan? waitFor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (waitFor is { } wait) MultiKeyPop.ValidateWait(wait);
        var operation = waitFor.HasValue ? "BZMPOP" : "ZMPOP";
        var arguments = new RespireValue[keys.Length + (waitFor.HasValue ? 5 : 4)];
        var index = 0;
        if (waitFor is { } timeout) arguments[index++] = MultiKeyPop.ToSeconds(timeout);
        arguments[index++] = keys.Length;
        MultiKeyPop.CopyPopKeys(client, keys, arguments.AsSpan(index, keys.Length), operation);
        index += keys.Length;
        arguments[index++] = descending ? "MAX" : "MIN";
        arguments[index++] = "COUNT";
        arguments[index] = count;
        return (operation, new CmdN(waitFor.HasValue ? Verbs.BZMPop : Verbs.ZMPop, arguments));
    }

    private static (string Operation, CmdN Command) PopOneCommand(
        RespireClient client, ReadOnlySpan<RespireKey> keys, TimeSpan waitFor, bool descending)
    {
        MultiKeyPop.ValidateWait(waitFor);
        var operation = descending ? "BZPOPMAX" : "BZPOPMIN";
        var arguments = new RespireValue[keys.Length + 1];
        MultiKeyPop.CopyPopKeys(client, keys, arguments.AsSpan(0, keys.Length), operation);
        arguments[^1] = MultiKeyPop.ToSeconds(waitFor);
        return (operation, new CmdN(descending ? Verbs.BZPopMax : Verbs.BZPopMin, arguments));
    }

    private static void ValidatePopEntries(in RespValue entries)
    {
        if (entries.Type != RespDataType.Array || entries.AsArray().IsEmpty)
            throw new RespireProtocolException("Unexpected sorted-set pop entries shape.");
        foreach (ref readonly var pair in entries.AsArray())
            _ = PopReplyElements(in pair, 2);
    }

    private static ReadOnlySpan<RespValue> PopReplyElements(in RespValue reply, int count)
    {
        if (reply.Type != RespDataType.Array || reply.AsArray().Length != count)
            throw new RespireProtocolException("Unexpected sorted-set pop reply shape.");
        return reply.AsArray();
    }
}
