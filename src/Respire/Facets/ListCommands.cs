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
    {
        var owner = DispatchResponseSource<long?>.Start();
        try { return owner.Attach(PositionBorrowedAsync(key, value, rank, maxLength, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long?> PositionBorrowedAsync(RespireKey key, RespireValue value, long rank, long maxLength, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerOrNullAsync("LPOS", new Cmd1N(RespireCommands.List.LPOS.Verb, client.Key(in key), PositionArguments(value, rank, null, maxLength)), cancellationToken, observation: observation);

    public ValueTask<long[]> PositionsAsync(RespireKey key, RespireValue value, long count = 0, long rank = 1, long maxLength = 0, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long[]>.Start();
        try { return owner.Attach(PositionsBorrowedAsync(key, value, count, rank, maxLength, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long[]> PositionsBorrowedAsync(RespireKey key, RespireValue value, long count, long rank, long maxLength, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.ConvertResponseAsync("LPOS", new Cmd1N(RespireCommands.List.LPOS.Verb, client.Key(in key), PositionArguments(value, rank, count, maxLength)), cancellationToken,
            0, static (int _, in RespValue reply) => ResponseReader.IntegerArray(in reply), observation: observation);

    public ValueTask<long> InsertBeforeAsync(RespireKey key, RespireValue pivot, RespireValue value, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(InsertBeforeBorrowedAsync(key, pivot, value, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> InsertBeforeBorrowedAsync(RespireKey key, RespireValue pivot, RespireValue value, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => InsertAsync(key, "BEFORE", pivot, value, cancellationToken, observation);

    public ValueTask<long> InsertAfterAsync(RespireKey key, RespireValue pivot, RespireValue value, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(InsertAfterBorrowedAsync(key, pivot, value, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> InsertAfterBorrowedAsync(RespireKey key, RespireValue pivot, RespireValue value, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => InsertAsync(key, "AFTER", pivot, value, cancellationToken, observation);

    private ValueTask<long> InsertAsync(RespireKey key, RespireValue placement, RespireValue pivot, RespireValue value, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("LINSERT", new Cmd4(RespireCommands.List.LINSERT.Verb, client.Key(in key), placement, pivot, value), cancellationToken, observation: observation);

    public ValueTask<bool> SetAsync(RespireKey key, long index, RespireValue value, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetBorrowedAsync(key, index, value, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> SetBorrowedAsync(RespireKey key, long index, RespireValue value, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.OkResultAsync("LSET", new Cmd3(RespireCommands.List.LSET.Verb, client.Key(in key), index, value), cancellationToken, observation: observation);

    public ValueTask<long> LeftPushIfExistsAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(LeftPushIfExistsBorrowedAsync(key, values, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> LeftPushIfExistsBorrowedAsync(RespireKey key, ReadOnlySpan<RespireValue> values, RespireTelemetry.ErrorObservation observation)
        => LeftPushIfExistsBorrowedAsync(key, values, CancellationToken.None, observation);

    public ValueTask<long> LeftPushIfExistsAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(LeftPushIfExistsBorrowedAsync(key, values, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> LeftPushIfExistsBorrowedAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ValidatePushValues(values);
        return client.IntegerValuesAsync("LPUSHX", RespireCommands.List.LPUSHX.Verb, client.Key(in key), values, cancellationToken, observation: observation);
    }

    public ValueTask<long> RightPushIfExistsAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(RightPushIfExistsBorrowedAsync(key, values, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> RightPushIfExistsBorrowedAsync(RespireKey key, ReadOnlySpan<RespireValue> values, RespireTelemetry.ErrorObservation observation)
        => RightPushIfExistsBorrowedAsync(key, values, CancellationToken.None, observation);

    public ValueTask<long> RightPushIfExistsAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(RightPushIfExistsBorrowedAsync(key, values, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> RightPushIfExistsBorrowedAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ValidatePushValues(values);
        return client.IntegerValuesAsync("RPUSHX", RespireCommands.List.RPUSHX.Verb, client.Key(in key), values, cancellationToken, observation: observation);
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
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(LeftPushBorrowedAsync(key, values, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> LeftPushBorrowedAsync(RespireKey key, ReadOnlySpan<RespireValue> values, RespireTelemetry.ErrorObservation observation)
        => LeftPushBorrowedAsync(key, values, CancellationToken.None, observation);

    public ValueTask<long> LeftPushAsync(
        RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(LeftPushBorrowedAsync(key, values, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> LeftPushBorrowedAsync(
        RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerValuesAsync("LPUSH", Verbs.LPush, client.Key(in key), values, cancellationToken, observation: observation);

    public ValueTask<long> RightPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(RightPushBorrowedAsync(key, values, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> RightPushBorrowedAsync(RespireKey key, ReadOnlySpan<RespireValue> values, RespireTelemetry.ErrorObservation observation)
        => RightPushBorrowedAsync(key, values, CancellationToken.None, observation);

    public ValueTask<long> RightPushAsync(
        RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(RightPushBorrowedAsync(key, values, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> RightPushBorrowedAsync(
        RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerValuesAsync("RPUSH", Verbs.RPush, client.Key(in key), values, cancellationToken, observation: observation);

    public ValueTask<string?> LeftPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string?>.Start();
        try { return owner.Attach(LeftPopBorrowedAsync(key, waitFor, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?> LeftPopBorrowedAsync(RespireKey key, TimeSpan? waitFor, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => PopAsync(key, waitFor, Verbs.LPop, Verbs.BLPop, "LPOP", "BLPOP", cancellationToken, observation);

    public ValueTask<string?> RightPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string?>.Start();
        try { return owner.Attach(RightPopBorrowedAsync(key, waitFor, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?> RightPopBorrowedAsync(RespireKey key, TimeSpan? waitFor, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => PopAsync(key, waitFor, Verbs.RPop, Verbs.BRPop, "RPOP", "BRPOP", cancellationToken, observation);

    public ValueTask<string[]> LeftPopManyAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string[]>.Start();
        try { return owner.Attach(LeftPopManyBorrowedAsync(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string[]> LeftPopManyBorrowedAsync(
        RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => PopAsync(key, count, Verbs.LPop, "LPOP", cancellationToken, observation);

    public ValueTask<string[]> RightPopManyAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string[]>.Start();
        try { return owner.Attach(RightPopManyBorrowedAsync(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string[]> RightPopManyBorrowedAsync(
        RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => PopAsync(key, count, Verbs.RPop, "RPOP", cancellationToken, observation);

    private ValueTask<string[]> PopAsync(
        RespireKey key, long count, Verb verb, string operation, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return client.StringArrayAsync(
            operation, new Cmd2(verb, client.Key(in key), count), cancellationToken, observation: observation);
    }

    private ValueTask<string?> PopAsync(
        RespireKey key, TimeSpan? waitFor, Verb plain, Verb blocking, string plainName, string blockingName,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (waitFor is not { } wait)
        {
            return client.StringOrNullAsync(plainName, new Cmd1(plain, client.Key(in key)), cancellationToken, observation: observation);
        }

        return PopBlockingAsync(key, wait, blocking, blockingName, cancellationToken, observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<string?> PopBlockingAsync(
        RespireKey key, TimeSpan wait, Verb blocking, string blockingName, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        // BLPOP replies [key, value], or null on timeout.
        return await client.ConvertBlockingResponseAsync(
            blockingName, new Cmd2(blocking, client.Key(in key), ToSeconds(wait)), cancellationToken, 0,
            static (int _, in RespValue reply) => reply.IsNull ? null : reply.AsArray()[1].AsString(), observation: observation)
            .ConfigureAwait(false);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> LeftPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T?>.Start();
        try { return owner.Attach(LeftPopBorrowedAsync<T>(key, waitFor, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T?> LeftPopBorrowedAsync<T>(RespireKey key, TimeSpan? waitFor, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => PopAsync<T>(key, waitFor, Verbs.LPop, Verbs.BLPop, "LPOP", "BLPOP", cancellationToken, observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> RightPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T?>.Start();
        try { return owner.Attach(RightPopBorrowedAsync<T>(key, waitFor, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T?> RightPopBorrowedAsync<T>(RespireKey key, TimeSpan? waitFor, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => PopAsync<T>(key, waitFor, Verbs.RPop, Verbs.BRPop, "RPOP", "BRPOP", cancellationToken, observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> LeftPopManyAsync<T>(
        RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T[]>.Start();
        try { return owner.Attach(LeftPopManyBorrowedAsync<T>(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T[]> LeftPopManyBorrowedAsync<T>(
        RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => PopAsync<T>(key, count, Verbs.LPop, "LPOP", cancellationToken, observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RightPopManyAsync<T>(
        RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T[]>.Start();
        try { return owner.Attach(RightPopManyBorrowedAsync<T>(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T[]> RightPopManyBorrowedAsync<T>(
        RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => PopAsync<T>(key, count, Verbs.RPop, "RPOP", cancellationToken, observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T[]> PopAsync<T>(
        RespireKey key, long count, Verb verb, string operation, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return client.DeserializeArrayAsync<T, Cmd2>(
            operation, new Cmd2(verb, client.Key(in key), count), cancellationToken, observation: observation);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T?> PopAsync<T>(
        RespireKey key, TimeSpan? waitFor, Verb plain, Verb blocking, string plainName, string blockingName,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (waitFor is not { } wait)
        {
            return client.DeserializeAsync<T, Cmd1>(plainName, new Cmd1(plain, client.Key(in key)), cancellationToken, observation: observation);
        }

        return PopBlockingAsync<T>(key, wait, blocking, blockingName, cancellationToken, observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<T?> PopBlockingAsync<T>(
        RespireKey key, TimeSpan wait, Verb blocking, string blockingName, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        // BLPOP replies [key, value], or null on timeout.
        return await client.ConvertBlockingResponseAsync(
            blockingName, new Cmd2(blocking, client.Key(in key), ToSeconds(wait)), cancellationToken, client,
            static (RespireClient owner, in RespValue reply) => reply.IsNull ? default : owner.DeserializeBorrowed<T>(in reply.AsArray()[1]), observation: observation)
            .ConfigureAwait(false);
    }

    public ValueTask<string?> MoveAsync(
        RespireKey source, RespireKey destination, ListSide from = ListSide.Left, ListSide to = ListSide.Right,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string?>.Start();
        try { return owner.Attach(MoveBorrowedAsync(source, destination, from, to, waitFor, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?> MoveBorrowedAsync(
        RespireKey source, RespireKey destination, ListSide from, ListSide to,
        TimeSpan? waitFor, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        RespireValue fromSide = from == ListSide.Left ? "LEFT" : "RIGHT";
        RespireValue toSide = to == ListSide.Left ? "LEFT" : "RIGHT";
        if (waitFor is not { } wait)
        {
            return client.StringOrNullAsync(
                "LMOVE", new Cmd4(Verbs.LMove, client.Key(in source), client.Key(in destination), fromSide, toSide),
                cancellationToken, observation: observation);
        }

        return MoveBlockingAsync(source, destination, fromSide, toSide, wait, cancellationToken, observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<string?> MoveBlockingAsync(
        RespireKey source, RespireKey destination, RespireValue fromSide, RespireValue toSide, TimeSpan wait,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        return await client.ConvertBlockingResponseAsync(
            "BLMOVE",
            new Cmd5(Verbs.BLMove, client.Key(in source), client.Key(in destination), fromSide, toSide, ToSeconds(wait)),
            cancellationToken, 0, static (int _, in RespValue reply) => ResponseReader.StringOrNull(in reply), observation: observation)
            .ConfigureAwait(false);
    }

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(CountBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> CountBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("LLEN", new Cmd1(Verbs.LLen, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<string[]> RangeAsync(RespireKey key, long start = 0, long stop = -1, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string[]>.Start();
        try { return owner.Attach(RangeBorrowedAsync(key, start, stop, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string[]> RangeBorrowedAsync(RespireKey key, long start, long stop, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringArrayAsync("LRANGE", new Cmd3(Verbs.LRange, client.Key(in key), start, stop), cancellationToken, observation: observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RangeAsync<T>(
        RespireKey key, long start = 0, long stop = -1, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T[]>.Start();
        try { return owner.Attach(RangeBorrowedAsync<T>(key, start, stop, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T[]> RangeBorrowedAsync<T>(
        RespireKey key, long start, long stop, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.DeserializeArrayAsync<T, Cmd3>(
            "LRANGE", new Cmd3(Verbs.LRange, client.Key(in key), start, stop), cancellationToken, observation: observation);

    public ValueTask<string?> IndexAsync(RespireKey key, long index, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string?>.Start();
        try { return owner.Attach(IndexBorrowedAsync(key, index, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?> IndexBorrowedAsync(RespireKey key, long index, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringOrNullAsync("LINDEX", new Cmd2(Verbs.LIndex, client.Key(in key), index), cancellationToken, observation: observation);

    public ValueTask<long> RemoveAsync(RespireKey key, RespireValue value, long count = 0, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(RemoveBorrowedAsync(key, value, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> RemoveBorrowedAsync(RespireKey key, RespireValue value, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("LREM", new Cmd3(Verbs.LRem, client.Key(in key), count, value), cancellationToken, observation: observation);

    public ValueTask<bool> TrimAsync(RespireKey key, long start, long stop, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(TrimBorrowedAsync(key, start, stop, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> TrimBorrowedAsync(RespireKey key, long start, long stop, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.OkResultAsync("LTRIM", new Cmd3(Verbs.LTrim, client.Key(in key), start, stop), cancellationToken, observation: observation);

    /// <summary>Redis blocking timeouts are seconds (fractional allowed); 0 waits forever.</summary>
    internal static RespireValue ToSeconds(TimeSpan waitFor)
        => waitFor == Timeout.InfiniteTimeSpan ? 0 : Math.Max(waitFor.TotalSeconds, 0.001);
}
