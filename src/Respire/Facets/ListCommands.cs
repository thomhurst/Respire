using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Which end of a list an operation targets.</summary>
public enum ListSide
{
    /// <summary>The head, or left-hand side, of the list.</summary>
    Left,

    /// <summary>The tail, or right-hand side, of the list.</summary>
    Right,
}

/// <summary>
/// List commands. Collection cardinality uses <see cref="CountAsync"/>. The pop and move
/// operations accept an optional <c>waitFor</c>: when given,
/// the call becomes its blocking Redis variant (BLPOP, BLMOVE, …) and transparently runs on a
/// dedicated pooled connection, so blocking never stalls multiplexed traffic — use
/// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to wait indefinitely.
/// </summary>
public partial interface IListCommands
{
    /// <summary>First matching zero-based index, or null. Negative rank searches from the tail; maxLength 0 scans without a limit. Redis: LPOS.</summary>
    ValueTask<long?> PositionAsync(RespireKey key, RespireValue value, long rank = 1, long maxLength = 0, CancellationToken cancellationToken = default);

    /// <summary>Matching zero-based indexes in search order; empty when absent. Count 0 returns all matches, negative rank searches from the tail, maxLength 0 scans without a limit. Redis: LPOS COUNT.</summary>
    ValueTask<long[]> PositionsAsync(RespireKey key, RespireValue value, long count = 0, long rank = 1, long maxLength = 0, CancellationToken cancellationToken = default);

    /// <summary>Inserts before the first pivot; returns the new length, 0 for a missing key, or -1 for a missing pivot. Redis: LINSERT BEFORE.</summary>
    ValueTask<long> InsertBeforeAsync(RespireKey key, RespireValue pivot, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>Inserts after the first pivot; returns the new length, 0 for a missing key, or -1 for a missing pivot. Redis: LINSERT AFTER.</summary>
    ValueTask<long> InsertAfterAsync(RespireKey key, RespireValue pivot, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>Replaces an element; negative indexes count from the tail. Returns true on OK; missing keys and out-of-range indexes raise server errors. Redis: LSET.</summary>
    ValueTask<bool> SetAsync(RespireKey key, long index, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>Prepends values only to an existing list; returns the new length or 0 when missing. Redis: LPUSHX.</summary>
    ValueTask<long> LeftPushIfExistsAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Prepends values only to an existing list; returns the new length or 0 when missing. Redis: LPUSHX.</summary>
    ValueTask<long> LeftPushIfExistsAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken);

    /// <summary>Appends values only to an existing list; returns the new length or 0 when missing. Redis: RPUSHX.</summary>
    ValueTask<long> RightPushIfExistsAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Appends values only to an existing list; returns the new length or 0 when missing. Redis: RPUSHX.</summary>
    ValueTask<long> RightPushIfExistsAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken);

    /// <summary>Prepends values; returns the new length. Redis: LPUSH.</summary>
    ValueTask<long> LeftPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Prepends values; returns the new length. Redis: LPUSH.</summary>
    ValueTask<long> LeftPushAsync(
        RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken);

    /// <summary>Appends values; returns the new length. Redis: RPUSH.</summary>
    ValueTask<long> RightPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Appends values; returns the new length. Redis: RPUSH.</summary>
    ValueTask<long> RightPushAsync(
        RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken);

    /// <summary>
    /// Pops from the head. Null when the list is empty (after <paramref name="waitFor"/>, if
    /// given). Redis: LPOP / BLPOP.
    /// </summary>
    ValueTask<string?> LeftPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Pops from the tail. Redis: RPOP / BRPOP.</summary>
    ValueTask<string?> RightPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Removes and returns up to <paramref name="count"/> elements from the head. Redis: LPOP.</summary>
    ValueTask<string[]> LeftPopManyAsync(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>Removes and returns up to <paramref name="count"/> elements from the tail. Redis: RPOP.</summary>
    ValueTask<string[]> RightPopManyAsync(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pops from the head and deserializes as <typeparamref name="T"/>; default when the list is
    /// empty. Always call with an explicit type argument — that is what separates it from the
    /// <c>string?</c> overload. Redis: LPOP / BLPOP.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> LeftPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Pops from the tail and deserializes as <typeparamref name="T"/>. Redis: RPOP / BRPOP.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> RightPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes up to <paramref name="count"/> elements from the head and deserializes them as
    /// <typeparamref name="T"/>. Redis: LPOP.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T[]> LeftPopManyAsync<T>(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes up to <paramref name="count"/> elements from the tail and deserializes them as
    /// <typeparamref name="T"/>. Redis: RPOP.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T[]> RightPopManyAsync<T>(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically moves an element between lists and returns it; null when the source is empty.
    /// Redis: LMOVE / BLMOVE.
    /// </summary>
    ValueTask<string?> MoveAsync(
        RespireKey source,
        RespireKey destination,
        ListSide from = ListSide.Left,
        ListSide to = ListSide.Right,
        TimeSpan? waitFor = null,
        CancellationToken cancellationToken = default);

    /// <summary>Number of elements in the list (0 when missing). Redis: LLEN.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Elements between two indexes inclusive (negative counts from the end). Redis: LRANGE.</summary>
    ValueTask<string[]> RangeAsync(RespireKey key, long start = 0, long stop = -1, CancellationToken cancellationToken = default);

    /// <summary>Deserialized elements between two indexes inclusive. Redis: LRANGE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T[]> RangeAsync<T>(
        RespireKey key, long start = 0, long stop = -1, CancellationToken cancellationToken = default);

    /// <summary>The element at an index, or null out of range. Redis: LINDEX.</summary>
    ValueTask<string?> IndexAsync(RespireKey key, long index, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes occurrences of a value: count &gt; 0 from the head, &lt; 0 from the tail, 0 all.
    /// Returns how many were removed. Redis: LREM.
    /// </summary>
    ValueTask<long> RemoveAsync(RespireKey key, RespireValue value, long count = 0, CancellationToken cancellationToken = default);

    /// <summary>Trims the list to the inclusive index range; returns true on OK. Redis: LTRIM.</summary>
    ValueTask<bool> TrimAsync(RespireKey key, long start, long stop, CancellationToken cancellationToken = default);
}

internal sealed partial class ListCommands(RespireClient client) : IListCommands
{
    public ValueTask<long?> PositionAsync(RespireKey key, RespireValue value, long rank = 1, long maxLength = 0, CancellationToken cancellationToken = default)
        => client.IntegerOrNullAsync("LPOS", new Cmd1N(RespireCommands.List.LPOS.Verb, client.Key(in key), PositionArguments(value, rank, null, maxLength)), cancellationToken);

    public ValueTask<long[]> PositionsAsync(RespireKey key, RespireValue value, long count = 0, long rank = 1, long maxLength = 0, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("LPOS", new Cmd1N(RespireCommands.List.LPOS.Verb, client.Key(in key), PositionArguments(value, rank, count, maxLength)), cancellationToken,
            0, static (int _, in RespValue reply) => ResponseReader.IntegerArray(in reply));

    public ValueTask<long> InsertBeforeAsync(RespireKey key, RespireValue pivot, RespireValue value, CancellationToken cancellationToken = default)
        => InsertAsync(key, "BEFORE", pivot, value, cancellationToken);

    public ValueTask<long> InsertAfterAsync(RespireKey key, RespireValue pivot, RespireValue value, CancellationToken cancellationToken = default)
        => InsertAsync(key, "AFTER", pivot, value, cancellationToken);

    private ValueTask<long> InsertAsync(RespireKey key, RespireValue placement, RespireValue pivot, RespireValue value, CancellationToken cancellationToken)
        => client.IntegerAsync("LINSERT", new Cmd4(RespireCommands.List.LINSERT.Verb, client.Key(in key), placement, pivot, value), cancellationToken);

    public ValueTask<bool> SetAsync(RespireKey key, long index, RespireValue value, CancellationToken cancellationToken = default)
        => client.OkResultAsync("LSET", new Cmd3(RespireCommands.List.LSET.Verb, client.Key(in key), index, value), cancellationToken);

    public ValueTask<long> LeftPushIfExistsAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => LeftPushIfExistsAsync(key, values, CancellationToken.None);

    public ValueTask<long> LeftPushIfExistsAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
    {
        ValidatePushValues(values);
        return client.IntegerValuesAsync("LPUSHX", RespireCommands.List.LPUSHX.Verb, client.Key(in key), values, cancellationToken);
    }

    public ValueTask<long> RightPushIfExistsAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => RightPushIfExistsAsync(key, values, CancellationToken.None);

    public ValueTask<long> RightPushIfExistsAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
    {
        ValidatePushValues(values);
        return client.IntegerValuesAsync("RPUSHX", RespireCommands.List.RPUSHX.Verb, client.Key(in key), values, cancellationToken);
    }

    internal static void ValidatePushValues(ReadOnlySpan<RespireValue> values)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }
    }

    internal static RespireValue[] PositionArguments(RespireValue value, long rank, long? count, long maxLength)
    {
        if (rank is 0 or long.MinValue)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), rank, "Rank must be nonzero and greater than Int64.MinValue.");
        }
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        var arguments = new RespireValue[1 + (rank == 1 ? 0 : 2) + (count.HasValue ? 2 : 0) + (maxLength == 0 ? 0 : 2)];
        arguments[0] = value;
        var index = 1;
        if (rank != 1)
        {
            arguments[index++] = "RANK";
            arguments[index++] = rank;
        }
        if (count is { } matches)
        {
            arguments[index++] = "COUNT";
            arguments[index++] = matches;
        }
        if (maxLength != 0)
        {
            arguments[index++] = "MAXLEN";
            arguments[index] = maxLength;
        }
        return arguments;
    }

    public ValueTask<long> LeftPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => LeftPushAsync(key, values, CancellationToken.None);

    public ValueTask<long> LeftPushAsync(
        RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
        => client.IntegerValuesAsync("LPUSH", Verbs.LPush, client.Key(in key), values, cancellationToken);

    public ValueTask<long> RightPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => RightPushAsync(key, values, CancellationToken.None);

    public ValueTask<long> RightPushAsync(
        RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
        => client.IntegerValuesAsync("RPUSH", Verbs.RPush, client.Key(in key), values, cancellationToken);

    public ValueTask<string?> LeftPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => PopAsync(key, waitFor, Verbs.LPop, Verbs.BLPop, "LPOP", "BLPOP", cancellationToken);

    public ValueTask<string?> RightPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => PopAsync(key, waitFor, Verbs.RPop, Verbs.BRPop, "RPOP", "BRPOP", cancellationToken);

    public ValueTask<string[]> LeftPopManyAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
        => PopAsync(key, count, Verbs.LPop, "LPOP", cancellationToken);

    public ValueTask<string[]> RightPopManyAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
        => PopAsync(key, count, Verbs.RPop, "RPOP", cancellationToken);

    private ValueTask<string[]> PopAsync(
        RespireKey key, long count, Verb verb, string operation, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return client.StringArrayAsync(
            operation, new Cmd2(verb, client.Key(in key), count), cancellationToken);
    }

    private ValueTask<string?> PopAsync(
        RespireKey key, TimeSpan? waitFor, Verb plain, Verb blocking, string plainName, string blockingName,
        CancellationToken cancellationToken)
    {
        if (waitFor is not { } wait)
        {
            return client.StringOrNullAsync(plainName, new Cmd1(plain, client.Key(in key)), cancellationToken);
        }

        return PopBlockingAsync(key, wait, blocking, blockingName, cancellationToken);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<string?> PopBlockingAsync(
        RespireKey key, TimeSpan wait, Verb blocking, string blockingName, CancellationToken cancellationToken)
    {
        // BLPOP replies [key, value], or null on timeout.
        var reply = await client.SendBlockingAsync(
            blockingName, new Cmd2(blocking, client.Key(in key), ToSeconds(wait)), cancellationToken).ConfigureAwait(false);
        var popped = reply.IsNull ? null : reply.AsArray()[1].AsString();
        reply.Dispose();
        return popped;
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> LeftPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => PopAsync<T>(key, waitFor, Verbs.LPop, Verbs.BLPop, "LPOP", "BLPOP", cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> RightPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => PopAsync<T>(key, waitFor, Verbs.RPop, Verbs.BRPop, "RPOP", "BRPOP", cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> LeftPopManyAsync<T>(
        RespireKey key, long count, CancellationToken cancellationToken = default)
        => PopAsync<T>(key, count, Verbs.LPop, "LPOP", cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RightPopManyAsync<T>(
        RespireKey key, long count, CancellationToken cancellationToken = default)
        => PopAsync<T>(key, count, Verbs.RPop, "RPOP", cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T[]> PopAsync<T>(
        RespireKey key, long count, Verb verb, string operation, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return client.DeserializeArrayAsync<T, Cmd2>(
            operation, new Cmd2(verb, client.Key(in key), count), cancellationToken);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T?> PopAsync<T>(
        RespireKey key, TimeSpan? waitFor, Verb plain, Verb blocking, string plainName, string blockingName,
        CancellationToken cancellationToken)
    {
        if (waitFor is not { } wait)
        {
            return client.DeserializeAsync<T, Cmd1>(plainName, new Cmd1(plain, client.Key(in key)), cancellationToken);
        }

        return PopBlockingAsync<T>(key, wait, blocking, blockingName, cancellationToken);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<T?> PopBlockingAsync<T>(
        RespireKey key, TimeSpan wait, Verb blocking, string blockingName, CancellationToken cancellationToken)
    {
        // BLPOP replies [key, value], or null on timeout.
        var reply = await client.SendBlockingAsync(
            blockingName, new Cmd2(blocking, client.Key(in key), ToSeconds(wait)), cancellationToken).ConfigureAwait(false);
        try
        {
            return reply.IsNull ? default : client.DeserializeBorrowed<T>(in reply.AsArray()[1]);
        }
        finally
        {
            reply.Dispose();
        }
    }

    public ValueTask<string?> MoveAsync(
        RespireKey source, RespireKey destination, ListSide from = ListSide.Left, ListSide to = ListSide.Right,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        RespireValue fromSide = from == ListSide.Left ? "LEFT" : "RIGHT";
        RespireValue toSide = to == ListSide.Left ? "LEFT" : "RIGHT";
        if (waitFor is not { } wait)
        {
            return client.StringOrNullAsync(
                "LMOVE", new Cmd4(Verbs.LMove, client.Key(in source), client.Key(in destination), fromSide, toSide),
                cancellationToken);
        }

        return MoveBlockingAsync(source, destination, fromSide, toSide, wait, cancellationToken);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<string?> MoveBlockingAsync(
        RespireKey source, RespireKey destination, RespireValue fromSide, RespireValue toSide, TimeSpan wait,
        CancellationToken cancellationToken)
    {
        var reply = await client.SendBlockingAsync(
            "BLMOVE",
            new Cmd5(Verbs.BLMove, client.Key(in source), client.Key(in destination), fromSide, toSide, ToSeconds(wait)),
            cancellationToken).ConfigureAwait(false);
        var moved = ResponseReader.StringOrNull(in reply);
        reply.Dispose();
        return moved;
    }

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("LLEN", new Cmd1(Verbs.LLen, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> RangeAsync(RespireKey key, long start = 0, long stop = -1, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("LRANGE", new Cmd3(Verbs.LRange, client.Key(in key), start, stop), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RangeAsync<T>(
        RespireKey key, long start = 0, long stop = -1, CancellationToken cancellationToken = default)
        => client.DeserializeArrayAsync<T, Cmd3>(
            "LRANGE", new Cmd3(Verbs.LRange, client.Key(in key), start, stop), cancellationToken);

    public ValueTask<string?> IndexAsync(RespireKey key, long index, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("LINDEX", new Cmd2(Verbs.LIndex, client.Key(in key), index), cancellationToken);

    public ValueTask<long> RemoveAsync(RespireKey key, RespireValue value, long count = 0, CancellationToken cancellationToken = default)
        => client.IntegerAsync("LREM", new Cmd3(Verbs.LRem, client.Key(in key), count, value), cancellationToken);

    public ValueTask<bool> TrimAsync(RespireKey key, long start, long stop, CancellationToken cancellationToken = default)
        => client.OkResultAsync("LTRIM", new Cmd3(Verbs.LTrim, client.Key(in key), start, stop), cancellationToken);

    /// <summary>Redis blocking timeouts are seconds (fractional allowed); 0 waits forever.</summary>
    internal static RespireValue ToSeconds(TimeSpan waitFor)
        => waitFor == Timeout.InfiniteTimeSpan ? 0 : Math.Max(waitFor.TotalSeconds, 0.001);
}
