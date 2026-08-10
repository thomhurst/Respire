using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>Which end of a list an operation targets.</summary>
public enum ListSide
{
    Left,
    Right,
}

/// <summary>
/// List commands. The pop and move operations accept an optional <c>waitFor</c>: when given,
/// the call becomes its blocking Redis variant (BLPOP, BLMOVE, …) and transparently runs on a
/// dedicated pooled connection, so blocking never stalls multiplexed traffic — use
/// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to wait indefinitely.
/// </summary>
public interface IListCommands
{
    /// <summary>Prepends values; returns the new length. Redis: LPUSH.</summary>
    ValueTask<long> LeftPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Appends values; returns the new length. Redis: RPUSH.</summary>
    ValueTask<long> RightPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>
    /// Pops from the head. Null when the list is empty (after <paramref name="waitFor"/>, if
    /// given). Redis: LPOP / BLPOP.
    /// </summary>
    ValueTask<string?> LeftPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Pops from the tail. Redis: RPOP / BRPOP.</summary>
    ValueTask<string?> RightPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pops from the head and deserializes as <typeparamref name="T"/>; default when the list is
    /// empty. Always call with an explicit type argument — that is what separates it from the
    /// <c>string?</c> overload. Redis: LPOP / BLPOP.
    /// </summary>
    ValueTask<T?> LeftPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Pops from the tail and deserializes as <typeparamref name="T"/>. Redis: RPOP / BRPOP.</summary>
    ValueTask<T?> RightPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

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

    /// <summary>List length (0 when missing). Redis: LLEN.</summary>
    ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Elements between two indexes inclusive (negative counts from the end). Redis: LRANGE.</summary>
    ValueTask<string[]> RangeAsync(RespireKey key, long start = 0, long stop = -1, CancellationToken cancellationToken = default);

    /// <summary>The element at an index, or null out of range. Redis: LINDEX.</summary>
    ValueTask<string?> IndexAsync(RespireKey key, long index, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes occurrences of a value: count &gt; 0 from the head, &lt; 0 from the tail, 0 all.
    /// Returns how many were removed. Redis: LREM.
    /// </summary>
    ValueTask<long> RemoveAsync(RespireKey key, RespireValue value, long count = 0, CancellationToken cancellationToken = default);

    /// <summary>Trims the list to the inclusive index range. Redis: LTRIM.</summary>
    ValueTask TrimAsync(RespireKey key, long start, long stop, CancellationToken cancellationToken = default);
}

internal sealed class ListCommands(RespireClient client) : IListCommands
{
    public ValueTask<long> LeftPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => client.IntegerValuesAsync("LPUSH", Verbs.LPush, client.Key(in key), values);

    public ValueTask<long> RightPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => client.IntegerValuesAsync("RPUSH", Verbs.RPush, client.Key(in key), values);

    public ValueTask<string?> LeftPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => PopAsync(key, waitFor, Verbs.LPop, Verbs.BLPop, "LPOP", "BLPOP", cancellationToken);

    public ValueTask<string?> RightPopAsync(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => PopAsync(key, waitFor, Verbs.RPop, Verbs.BRPop, "RPOP", "BRPOP", cancellationToken);

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

    public ValueTask<T?> LeftPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => PopAsync<T>(key, waitFor, Verbs.LPop, Verbs.BLPop, "LPOP", "BLPOP", cancellationToken);

    public ValueTask<T?> RightPopAsync<T>(RespireKey key, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => PopAsync<T>(key, waitFor, Verbs.RPop, Verbs.BRPop, "RPOP", "BRPOP", cancellationToken);

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
    private async ValueTask<T?> PopBlockingAsync<T>(
        RespireKey key, TimeSpan wait, Verb blocking, string blockingName, CancellationToken cancellationToken)
    {
        // BLPOP replies [key, value], or null on timeout.
        var reply = await client.SendBlockingAsync(
            blockingName, new Cmd2(blocking, client.Key(in key), ToSeconds(wait)), cancellationToken).ConfigureAwait(false);
        var popped = reply.IsNull ? default : client.DeserializeBorrowed<T>(in reply.AsArray()[1]);
        reply.Dispose();
        return popped;
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

    public ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("LLEN", new Cmd1(Verbs.LLen, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> RangeAsync(RespireKey key, long start = 0, long stop = -1, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("LRANGE", new Cmd3(Verbs.LRange, client.Key(in key), start, stop), cancellationToken);

    public ValueTask<string?> IndexAsync(RespireKey key, long index, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("LINDEX", new Cmd2(Verbs.LIndex, client.Key(in key), index), cancellationToken);

    public ValueTask<long> RemoveAsync(RespireKey key, RespireValue value, long count = 0, CancellationToken cancellationToken = default)
        => client.IntegerAsync("LREM", new Cmd3(Verbs.LRem, client.Key(in key), count, value), cancellationToken);

    public ValueTask TrimAsync(RespireKey key, long start, long stop, CancellationToken cancellationToken = default)
        => client.OkAsync("LTRIM", new Cmd3(Verbs.LTrim, client.Key(in key), start, stop), cancellationToken);

    /// <summary>Redis blocking timeouts are seconds (fractional allowed); 0 waits forever.</summary>
    internal static RespireValue ToSeconds(TimeSpan waitFor)
        => waitFor == Timeout.InfiniteTimeSpan ? 0 : Math.Max(waitFor.TotalSeconds, 0.001);
}
