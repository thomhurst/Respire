namespace Respire;

internal sealed partial class StringCommands
{
    public ValueTask<bool> SetAsync(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, (RespireValue)value, expiry, when, cancellationToken);

    public ValueTask<bool> SetConditionalAsync(
        RespireKey key, string? value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default)
        => SetConditionalAsync(key, (RespireValue)value, condition, expiry, cancellationToken);

    public ValueTask<bool> SetConditionalAsync(
        RespireKey key, byte[]? value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default)
        => SetConditionalAsync(key, (RespireValue)value, condition, expiry, cancellationToken);

    public ValueTask<bool> SetConditionalAsync(
        RespireKey key, ReadOnlyMemory<byte> value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default)
        => SetConditionalAsync(key, (RespireValue)value, condition, expiry, cancellationToken);

    public ValueTask<bool> SetConditionalAsync(
        RespireKey key, Memory<byte> value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default)
        => SetConditionalAsync(key, (RespireValue)value, condition, expiry, cancellationToken);

    public ValueTask<bool> SetConditionalAsync(
        RespireKey key, ArraySegment<byte> value, RespireValueCondition condition, RespireExpiry expiry = default,
        CancellationToken cancellationToken = default)
        => SetConditionalAsync(key, (RespireValue)value, condition, expiry, cancellationToken);
}

internal sealed partial class HashCommands
{
    public ValueTask<bool> SetAsync(RespireKey key, string field, string? value, CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, cancellationToken);

    public ValueTask<bool> SetAsync(RespireKey key, string field, byte[]? value, CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, cancellationToken);

    public ValueTask<bool> SetAsync(RespireKey key, string field, Memory<byte> value, CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, ArraySegment<byte> value, CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, string? value, RespireSetWhen when,
        CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, byte[]? value, RespireSetWhen when,
        CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, ReadOnlyMemory<byte> value, RespireSetWhen when,
        CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, Memory<byte> value, RespireSetWhen when,
        CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, ArraySegment<byte> value, RespireSetWhen when,
        CancellationToken cancellationToken = default)
        => SetAsync(key, field, (RespireValue)value, when, cancellationToken);
}

internal sealed partial class ArrayCommands
{
    public ValueTask<ulong> SetAsync(RespireKey key, ulong index, string? value, CancellationToken cancellationToken = default)
        => SetAsync(key, index, (RespireValue)value, cancellationToken);

    public ValueTask<ulong> SetAsync(
        RespireKey key, ulong index, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        => SetAsync(key, index, (RespireValue)value, cancellationToken);

    public ValueTask<ulong> SetAsync(RespireKey key, ulong index, Memory<byte> value, CancellationToken cancellationToken = default)
        => SetAsync(key, index, (RespireValue)value, cancellationToken);

    public ValueTask<ulong> SetAsync(
        RespireKey key, ulong index, ArraySegment<byte> value, CancellationToken cancellationToken = default)
        => SetAsync(key, index, (RespireValue)value, cancellationToken);
}

internal sealed partial class SetCommands
{
    public ValueTask<bool> ContainsAsync(RespireKey key, string? member, CancellationToken cancellationToken = default)
        => ContainsAsync(key, (RespireValue)member, cancellationToken);

    public ValueTask<bool> ContainsAsync(RespireKey key, byte[]? member, CancellationToken cancellationToken = default)
        => ContainsAsync(key, (RespireValue)member, cancellationToken);

    public ValueTask<bool> ContainsAsync(RespireKey key, ReadOnlyMemory<byte> member, CancellationToken cancellationToken = default)
        => ContainsAsync(key, (RespireValue)member, cancellationToken);

    public ValueTask<bool> ContainsAsync(RespireKey key, Memory<byte> member, CancellationToken cancellationToken = default)
        => ContainsAsync(key, (RespireValue)member, cancellationToken);

    public ValueTask<bool> ContainsAsync(RespireKey key, ArraySegment<byte> member, CancellationToken cancellationToken = default)
        => ContainsAsync(key, (RespireValue)member, cancellationToken);
}

internal sealed partial class SortedSetCommands
{
    public ValueTask<bool> AddAsync(RespireKey key, string? member, double score, CancellationToken cancellationToken = default)
        => AddAsync(key, (RespireValue)member, score, cancellationToken);

    public ValueTask<bool> AddAsync(RespireKey key, byte[]? member, double score, CancellationToken cancellationToken = default)
        => AddAsync(key, (RespireValue)member, score, cancellationToken);

    public ValueTask<bool> AddAsync(
        RespireKey key, ReadOnlyMemory<byte> member, double score, CancellationToken cancellationToken = default)
        => AddAsync(key, (RespireValue)member, score, cancellationToken);

    public ValueTask<bool> AddAsync(RespireKey key, Memory<byte> member, double score, CancellationToken cancellationToken = default)
        => AddAsync(key, (RespireValue)member, score, cancellationToken);

    public ValueTask<bool> AddAsync(
        RespireKey key, ArraySegment<byte> member, double score, CancellationToken cancellationToken = default)
        => AddAsync(key, (RespireValue)member, score, cancellationToken);
}

internal sealed partial class BatchStringCommands
{
    public RespirePending<bool> Set(
        RespireKey key, string? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    public RespirePending<bool> Set(
        RespireKey key, byte[]? value, RespireExpiry expiry = default, RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    public RespirePending<bool> Set(
        RespireKey key, ReadOnlyMemory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    public RespirePending<bool> Set(
        RespireKey key, Memory<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    public RespirePending<bool> Set(
        RespireKey key, ArraySegment<byte> value, RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, (RespireValue)value, expiry, when);

    public RespirePending<bool> SetConditional(
        RespireKey key, string? value, RespireValueCondition condition, RespireExpiry expiry = default)
        => SetConditional(key, (RespireValue)value, condition, expiry);

    public RespirePending<bool> SetConditional(
        RespireKey key, byte[]? value, RespireValueCondition condition, RespireExpiry expiry = default)
        => SetConditional(key, (RespireValue)value, condition, expiry);

    public RespirePending<bool> SetConditional(
        RespireKey key, ReadOnlyMemory<byte> value, RespireValueCondition condition, RespireExpiry expiry = default)
        => SetConditional(key, (RespireValue)value, condition, expiry);

    public RespirePending<bool> SetConditional(
        RespireKey key, Memory<byte> value, RespireValueCondition condition, RespireExpiry expiry = default)
        => SetConditional(key, (RespireValue)value, condition, expiry);

    public RespirePending<bool> SetConditional(
        RespireKey key, ArraySegment<byte> value, RespireValueCondition condition, RespireExpiry expiry = default)
        => SetConditional(key, (RespireValue)value, condition, expiry);
}

internal sealed partial class BatchArrayCommands
{
    public RespirePending<ulong> Set(RespireKey key, ulong index, string? value)
        => Set(key, index, (ReadOnlySpan<RespireValue>)[(RespireValue)value]);

    public RespirePending<ulong> Set(RespireKey key, ulong index, ReadOnlyMemory<byte> value)
        => Set(key, index, (ReadOnlySpan<RespireValue>)[(RespireValue)value]);

    public RespirePending<ulong> Set(RespireKey key, ulong index, Memory<byte> value)
        => Set(key, index, (ReadOnlySpan<RespireValue>)[(RespireValue)value]);

    public RespirePending<ulong> Set(RespireKey key, ulong index, ArraySegment<byte> value)
        => Set(key, index, (ReadOnlySpan<RespireValue>)[(RespireValue)value]);
}

internal sealed partial class PathHashCommands
{
    public ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, string? value, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => SetAsync(key, path, field, (RespireValue)value, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, byte[]? value, RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => SetAsync(key, path, field, (RespireValue)value, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, ReadOnlyMemory<byte> value,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, path, field, (RespireValue)value, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, Memory<byte> value,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, path, field, (RespireValue)value, when, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, RespireKey path, RespireKey field, ArraySegment<byte> value,
        RespireSetWhen when = RespireSetWhen.Always, CancellationToken cancellationToken = default)
        => SetAsync(key, path, field, (RespireValue)value, when, cancellationToken);
}

internal sealed partial class SortedSetCommands
{
    public ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, string? member, double score,
        CancellationToken cancellationToken = default)
        => AddAsync(key, options, (RespireValue)member, score, cancellationToken);

    public ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, byte[]? member, double score,
        CancellationToken cancellationToken = default)
        => AddAsync(key, options, (RespireValue)member, score, cancellationToken);

    public ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, ReadOnlyMemory<byte> member, double score,
        CancellationToken cancellationToken = default)
        => AddAsync(key, options, (RespireValue)member, score, cancellationToken);

    public ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, Memory<byte> member, double score,
        CancellationToken cancellationToken = default)
        => AddAsync(key, options, (RespireValue)member, score, cancellationToken);

    public ValueTask<bool> AddAsync(
        RespireKey key, RespireSortedSetAddOptions options, ArraySegment<byte> member, double score,
        CancellationToken cancellationToken = default)
        => AddAsync(key, options, (RespireValue)member, score, cancellationToken);
}

internal sealed partial class BatchHashCommands
{
    public RespirePending<bool> Set(RespireKey key, string field, string? value)
        => Set(key, field, (RespireValue)value);

    public RespirePending<bool> Set(RespireKey key, string field, byte[]? value)
        => Set(key, field, (RespireValue)value);

    public RespirePending<bool> Set(RespireKey key, string field, ReadOnlyMemory<byte> value)
        => Set(key, field, (RespireValue)value);

    public RespirePending<bool> Set(RespireKey key, string field, Memory<byte> value)
        => Set(key, field, (RespireValue)value);

    public RespirePending<bool> Set(RespireKey key, string field, ArraySegment<byte> value)
        => Set(key, field, (RespireValue)value);
}

internal sealed partial class BatchHashCommands
{
    public RespirePending<bool> Set(RespireKey key, string field, string? value, RespireSetWhen when)
        => Set(key, field, (RespireValue)value, when);

    public RespirePending<bool> Set(RespireKey key, string field, byte[]? value, RespireSetWhen when)
        => Set(key, field, (RespireValue)value, when);

    public RespirePending<bool> Set(RespireKey key, string field, ReadOnlyMemory<byte> value, RespireSetWhen when)
        => Set(key, field, (RespireValue)value, when);

    public RespirePending<bool> Set(RespireKey key, string field, Memory<byte> value, RespireSetWhen when)
        => Set(key, field, (RespireValue)value, when);

    public RespirePending<bool> Set(RespireKey key, string field, ArraySegment<byte> value, RespireSetWhen when)
        => Set(key, field, (RespireValue)value, when);
}

internal sealed partial class BatchPathHashCommands
{
    public RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, string? value, RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, path, field, (RespireValue)value, when);

    public RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, byte[]? value, RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, path, field, (RespireValue)value, when);

    public RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, ReadOnlyMemory<byte> value,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, path, field, (RespireValue)value, when);

    public RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, Memory<byte> value,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, path, field, (RespireValue)value, when);

    public RespirePending<bool> Set(
        RespireKey key, RespireKey path, RespireKey field, ArraySegment<byte> value,
        RespireSetWhen when = RespireSetWhen.Always)
        => Set(key, path, field, (RespireValue)value, when);
}

internal sealed partial class BatchSetCommands
{
    public RespirePending<bool> Contains(RespireKey key, string? member)
        => Contains(key, (RespireValue)member);

    public RespirePending<bool> Contains(RespireKey key, byte[]? member)
        => Contains(key, (RespireValue)member);

    public RespirePending<bool> Contains(RespireKey key, ReadOnlyMemory<byte> member)
        => Contains(key, (RespireValue)member);

    public RespirePending<bool> Contains(RespireKey key, Memory<byte> member)
        => Contains(key, (RespireValue)member);

    public RespirePending<bool> Contains(RespireKey key, ArraySegment<byte> member)
        => Contains(key, (RespireValue)member);
}

internal sealed partial class BatchSortedSetCommands
{
    public RespirePending<bool> Add(RespireKey key, string? member, double score)
        => Add(key, (RespireValue)member, score);

    public RespirePending<bool> Add(RespireKey key, byte[]? member, double score)
        => Add(key, (RespireValue)member, score);

    public RespirePending<bool> Add(RespireKey key, ReadOnlyMemory<byte> member, double score)
        => Add(key, (RespireValue)member, score);

    public RespirePending<bool> Add(RespireKey key, Memory<byte> member, double score)
        => Add(key, (RespireValue)member, score);

    public RespirePending<bool> Add(RespireKey key, ArraySegment<byte> member, double score)
        => Add(key, (RespireValue)member, score);
}

internal sealed partial class BatchSortedSetCommands
{
    public RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, string? member, double score)
        => Add(key, options, (RespireValue)member, score);

    public RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, byte[]? member, double score)
        => Add(key, options, (RespireValue)member, score);

    public RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, ReadOnlyMemory<byte> member, double score)
        => Add(key, options, (RespireValue)member, score);

    public RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, Memory<byte> member, double score)
        => Add(key, options, (RespireValue)member, score);

    public RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, ArraySegment<byte> member, double score)
        => Add(key, options, (RespireValue)member, score);
}
