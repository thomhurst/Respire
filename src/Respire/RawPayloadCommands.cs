namespace Respire;

// Exact payload overloads keep text and binary writes trim-safe without changing typed scalar serialization.

public partial interface IRespireClient
{
    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);
}

public sealed partial class RespireClient
{
    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public ValueTask<bool> SetAsync(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public ValueTask<bool> SetAsync(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public ValueTask<bool> SetAsync(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public ValueTask<bool> SetAsync(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public ValueTask<bool> SetAsync(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);
}

public partial interface IRespireStringCommands
{
    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetConditionalAsync(
        RespireKey key, string? value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetConditionalAsync(
        RespireKey key, byte[]? value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetConditionalAsync(
        RespireKey key, ReadOnlyMemory<byte> value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetConditionalAsync(
        RespireKey key, Memory<byte> value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetConditionalAsync(
        RespireKey key, ArraySegment<byte> value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default);
}

public partial interface IRespireHashCommands
{
    /// <summary>Sets a raw payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(RespireKey key, string field, string? value, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(RespireKey key, string field, byte[]? value, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(RespireKey key, string field, Memory<byte> value, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, ArraySegment<byte> value, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, string? value, RespireSetWhen when,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, byte[]? value, RespireSetWhen when,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, ReadOnlyMemory<byte> value, RespireSetWhen when,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, Memory<byte> value, RespireSetWhen when,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, ArraySegment<byte> value, RespireSetWhen when,
        CancellationToken cancellationToken = default);
}

public partial interface IRespireArrayCommands
{
    /// <summary>Sets a raw payload. Redis: ARSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<ulong> SetAsync(RespireKey key, ulong index, string? value, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: ARSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<ulong> SetAsync(
        RespireKey key, ulong index, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: ARSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<ulong> SetAsync(RespireKey key, ulong index, Memory<byte> value, CancellationToken cancellationToken = default);

    /// <summary>Sets a raw payload. Redis: ARSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<ulong> SetAsync(
        RespireKey key, ulong index, ArraySegment<byte> value, CancellationToken cancellationToken = default);
}

public partial interface IRespireSetCommands
{
    /// <summary>Tests a raw payload for membership. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> ContainsAsync(RespireKey key, string? member, CancellationToken cancellationToken = default);

    /// <summary>Tests a raw payload for membership. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> ContainsAsync(RespireKey key, byte[]? member, CancellationToken cancellationToken = default);

    /// <summary>Tests a raw payload for membership. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> ContainsAsync(RespireKey key, ReadOnlyMemory<byte> member, CancellationToken cancellationToken = default);

    /// <summary>Tests a raw payload for membership. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> ContainsAsync(RespireKey key, Memory<byte> member, CancellationToken cancellationToken = default);

    /// <summary>Tests a raw payload for membership. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> ContainsAsync(RespireKey key, ArraySegment<byte> member, CancellationToken cancellationToken = default);
}

public partial interface IRespireSortedSetCommands
{
    /// <summary>Adds a raw payload with a score. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(RespireKey key, string? member, double score, CancellationToken cancellationToken = default);

    /// <summary>Adds a raw payload with a score. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(RespireKey key, byte[]? member, double score, CancellationToken cancellationToken = default);

    /// <summary>Adds a raw payload with a score. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(
        RespireKey key, ReadOnlyMemory<byte> member, double score, CancellationToken cancellationToken = default);

    /// <summary>Adds a raw payload with a score. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(RespireKey key, Memory<byte> member, double score, CancellationToken cancellationToken = default);

    /// <summary>Adds a raw payload with a score. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(
        RespireKey key, ArraySegment<byte> member, double score, CancellationToken cancellationToken = default);
}

public partial interface IRespireCommandQueue
{
    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always);
}

public sealed partial class RespireBatch
{
    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);
}

public abstract partial class RespireTransactionBase
{
    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    public RespirePending<bool> Set(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);
}

public partial interface IRespireBatchStringCommands
{
    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> SetConditional(
        RespireKey key, string? value, RespireValueCondition condition, RespireExpiry expiry = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> SetConditional(
        RespireKey key, byte[]? value, RespireValueCondition condition, RespireExpiry expiry = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> SetConditional(
        RespireKey key, ReadOnlyMemory<byte> value, RespireValueCondition condition, RespireExpiry expiry = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> SetConditional(
        RespireKey key, Memory<byte> value, RespireValueCondition condition, RespireExpiry expiry = default);

    /// <summary>Sets a raw payload only when the comparison succeeds. Redis: SET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> SetConditional(
        RespireKey key, ArraySegment<byte> value, RespireValueCondition condition, RespireExpiry expiry = default);
}

public partial interface IRespireBatchArrayCommands
{
    /// <summary>Sets a raw payload. Redis: ARSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<ulong> Set(RespireKey key, ulong index, string? value);

    /// <summary>Sets a raw payload. Redis: ARSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<ulong> Set(RespireKey key, ulong index, ReadOnlyMemory<byte> value);

    /// <summary>Sets a raw payload. Redis: ARSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<ulong> Set(RespireKey key, ulong index, Memory<byte> value);

    /// <summary>Sets a raw payload. Redis: ARSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<ulong> Set(RespireKey key, ulong index, ArraySegment<byte> value);
}

public partial interface IRespirePathHashCommands
{
    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, string? value, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, byte[]? value, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, ReadOnlyMemory<byte> value,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);

    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, Memory<byte> value,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);

    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, ArraySegment<byte> value,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default);
}

public partial interface IRespireSortedSetCommands
{
    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, string? member, double score,
        CancellationToken cancellationToken = default);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, byte[]? member, double score,
        CancellationToken cancellationToken = default);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, ReadOnlyMemory<byte> member, double score,
        CancellationToken cancellationToken = default);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, Memory<byte> member, double score,
        CancellationToken cancellationToken = default);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, ArraySegment<byte> member, double score,
        CancellationToken cancellationToken = default);
}

public partial interface IRespireBatchHashCommands
{
    /// <summary>Uses a raw text or binary payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, string? value);

    /// <summary>Uses a raw text or binary payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, byte[]? value);

    /// <summary>Uses a raw text or binary payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, ReadOnlyMemory<byte> value);

    /// <summary>Uses a raw text or binary payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, Memory<byte> value);

    /// <summary>Uses a raw text or binary payload. Redis: HSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, ArraySegment<byte> value);
}

public partial interface IRespireBatchHashCommands
{
    /// <summary>Uses a raw text or binary payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, string? value, RespireSetWhen when);

    /// <summary>Uses a raw text or binary payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, byte[]? value, RespireSetWhen when);

    /// <summary>Uses a raw text or binary payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, ReadOnlyMemory<byte> value, RespireSetWhen when);

    /// <summary>Uses a raw text or binary payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, Memory<byte> value, RespireSetWhen when);

    /// <summary>Uses a raw text or binary payload. Redis: HSET/HSETNX/HSETEX.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(RespireKey key, string field, ArraySegment<byte> value, RespireSetWhen when);
}

public partial interface IRespireBatchPathHashCommands
{
    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, string? value, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, byte[]? value, RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, ReadOnlyMemory<byte> value,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, Memory<byte> value,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <summary>Uses a raw text or binary payload. Redis: PHSET.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, ArraySegment<byte> value,
        RespireSetWhen when = RespireSetWhen.Always);
}

public partial interface IRespireBatchSetCommands
{
    /// <summary>Uses a raw text or binary payload. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Contains(RespireKey key, string? member);

    /// <summary>Uses a raw text or binary payload. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Contains(RespireKey key, byte[]? member);

    /// <summary>Uses a raw text or binary payload. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Contains(RespireKey key, ReadOnlyMemory<byte> member);

    /// <summary>Uses a raw text or binary payload. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Contains(RespireKey key, Memory<byte> member);

    /// <summary>Uses a raw text or binary payload. Redis: SISMEMBER.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Contains(RespireKey key, ArraySegment<byte> member);
}

public partial interface IRespireBatchSortedSetCommands
{
    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, string? member, double score);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, byte[]? member, double score);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, ReadOnlyMemory<byte> member, double score);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, Memory<byte> member, double score);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, ArraySegment<byte> member, double score);
}

public partial interface IRespireBatchSortedSetCommands
{
    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, string? member, double score);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, byte[]? member, double score);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, ReadOnlyMemory<byte> member, double score);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, Memory<byte> member, double score);

    /// <summary>Uses a raw text or binary payload. Redis: ZADD.</summary>
    /// <remarks>Binary buffers must remain unchanged until completion; for queued writes, until execution completes.</remarks>
    RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, ArraySegment<byte> member, double score);
}
