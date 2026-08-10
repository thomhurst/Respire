using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// List commands queued on a <see cref="RespireBatch"/> or <see cref="RespireTransaction"/>.
/// Mirrors <see cref="IListCommands"/> without its <c>waitFor</c> parameters: the blocking
/// variants (BLPOP, BLMOVE, …) have no deferred form because a batch cannot block.
/// </summary>
public interface IBatchListCommands
{
    /// <summary>Prepends values; returns the new length. Redis: LPUSH.</summary>
    RespirePending<long> LeftPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Appends values; returns the new length. Redis: RPUSH.</summary>
    RespirePending<long> RightPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Pops from the head; null when the list is empty. Redis: LPOP.</summary>
    RespirePending<string?> LeftPopAsync(RespireKey key);

    /// <summary>Pops from the tail; null when the list is empty. Redis: RPOP.</summary>
    RespirePending<string?> RightPopAsync(RespireKey key);

    /// <summary>
    /// Atomically moves an element between lists and returns it; null when the source is empty.
    /// Redis: LMOVE.
    /// </summary>
    RespirePending<string?> MoveAsync(
        RespireKey source, RespireKey destination, ListSide from = ListSide.Left, ListSide to = ListSide.Right);

    /// <summary>List length (0 when missing). Redis: LLEN.</summary>
    RespirePending<long> LengthAsync(RespireKey key);

    /// <summary>Elements between two indexes inclusive (negative counts from the end). Redis: LRANGE.</summary>
    RespirePending<string[]> RangeAsync(RespireKey key, long start = 0, long stop = -1);

    /// <summary>The element at an index, or null out of range. Redis: LINDEX.</summary>
    RespirePending<string?> IndexAsync(RespireKey key, long index);

    /// <summary>
    /// Removes occurrences of a value: count &gt; 0 from the head, &lt; 0 from the tail, 0 all.
    /// Returns how many were removed. Redis: LREM.
    /// </summary>
    RespirePending<long> RemoveAsync(RespireKey key, RespireValue value, long count = 0);

    /// <summary>Trims the list to the inclusive index range; true once the server replies OK. Redis: LTRIM.</summary>
    RespirePending<bool> TrimAsync(RespireKey key, long start, long stop);
}

internal sealed class BatchListCommands(IPendingSink sink) : IBatchListCommands
{
    public RespirePending<long> LeftPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => sink.Add<Cmd1N, long>(
            "LPUSH", new Cmd1N(Verbs.LPush, sink.Client.Key(in key), values.ToArray()),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> RightPushAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => sink.Add<Cmd1N, long>(
            "RPUSH", new Cmd1N(Verbs.RPush, sink.Client.Key(in key), values.ToArray()),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string?> LeftPopAsync(RespireKey key)
        => sink.Add<Cmd1, string?>(
            "LPOP", new Cmd1(Verbs.LPop, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<string?> RightPopAsync(RespireKey key)
        => sink.Add<Cmd1, string?>(
            "RPOP", new Cmd1(Verbs.RPop, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<string?> MoveAsync(
        RespireKey source, RespireKey destination, ListSide from = ListSide.Left, ListSide to = ListSide.Right)
    {
        RespireValue fromSide = from == ListSide.Left ? "LEFT" : "RIGHT";
        RespireValue toSide = to == ListSide.Left ? "LEFT" : "RIGHT";
        return sink.Add<Cmd4, string?>(
            "LMOVE",
            new Cmd4(Verbs.LMove, sink.Client.Key(in source), sink.Client.Key(in destination), fromSide, toSide),
            source, destination,
            static (c, v) => ResponseReader.StringOrNull(in v));
    }

    public RespirePending<long> LengthAsync(RespireKey key)
        => sink.Add<Cmd1, long>(
            "LLEN", new Cmd1(Verbs.LLen, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string[]> RangeAsync(RespireKey key, long start = 0, long stop = -1)
        => sink.Add<Cmd3, string[]>(
            "LRANGE", new Cmd3(Verbs.LRange, sink.Client.Key(in key), start, stop),
            static (c, v) => ResponseReader.StringArray(in v));

    public RespirePending<string?> IndexAsync(RespireKey key, long index)
        => sink.Add<Cmd2, string?>(
            "LINDEX", new Cmd2(Verbs.LIndex, sink.Client.Key(in key), index),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<long> RemoveAsync(RespireKey key, RespireValue value, long count = 0)
        => sink.Add<Cmd3, long>(
            "LREM", new Cmd3(Verbs.LRem, sink.Client.Key(in key), count, value),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> TrimAsync(RespireKey key, long start, long stop)
        => sink.Add<Cmd3, bool>(
            "LTRIM", new Cmd3(Verbs.LTrim, sink.Client.Key(in key), start, stop),
            static (c, v) => ResponseReader.Ok(in v));
}
