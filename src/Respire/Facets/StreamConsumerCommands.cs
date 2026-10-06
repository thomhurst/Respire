using Respire.Commands;
using Respire.Protocol;

namespace Respire;

public partial interface IStreamCommands
{
    /// <summary>Lists pending entries with optional idle, consumer, and ID filters. Redis: XPENDING.</summary>
    ValueTask<RespireStreamPendingEntry[]> PendingAsync(StreamPendingOptions options, RespireKey key,
        string group, CancellationToken cancellationToken = default);

    /// <summary>Transfers pending entries with optional delivery metadata changes. Redis: XCLAIM.</summary>
    ValueTask<RespireStreamEntry[]> ClaimAsync(StreamClaimOptions options, RespireKey key, string group,
        string consumer, TimeSpan minIdle, params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Transfers pending entries with optional delivery metadata changes and cancellation. Redis: XCLAIM.</summary>
    ValueTask<RespireStreamEntry[]> ClaimAsync(StreamClaimOptions options, RespireKey key, string group,
        string consumer, TimeSpan minIdle, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);

    /// <summary>Transfers pending IDs without incrementing delivery counters. Redis: XCLAIM JUSTID.</summary>
    ValueTask<RespireStreamId[]> ClaimIdsAsync(RespireKey key, string group, string consumer,
        TimeSpan minIdle, params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Transfers pending IDs without incrementing delivery counters, with cancellation. Redis: XCLAIM JUSTID.</summary>
    ValueTask<RespireStreamId[]> ClaimIdsAsync(RespireKey key, string group, string consumer,
        TimeSpan minIdle, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);

    /// <summary>Transfers pending IDs and applies optional metadata changes. Redis: XCLAIM JUSTID.</summary>
    /// <remarks>Delivery counters do not increase automatically; RetryCount can explicitly set them.</remarks>
    ValueTask<RespireStreamId[]> ClaimIdsAsync(StreamClaimOptions options, RespireKey key, string group,
        string consumer, TimeSpan minIdle, params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Transfers pending IDs with optional metadata changes and cancellation. Redis: XCLAIM JUSTID.</summary>
    ValueTask<RespireStreamId[]> ClaimIdsAsync(StreamClaimOptions options, RespireKey key, string group,
        string consumer, TimeSpan minIdle, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);

    /// <summary>Scans and transfers pending IDs without incrementing delivery counters. Redis: XAUTOCLAIM JUSTID (6.2+).</summary>
    ValueTask<RespireStreamClaimIdsResult> ClaimPendingIdsAsync(RespireKey key, string group, string consumer,
        TimeSpan minIdle, RespireStreamId? start = null, int count = 100, CancellationToken cancellationToken = default);
}

internal sealed partial class StreamCommands
{
    public ValueTask<RespireStreamPendingEntry[]> PendingAsync(StreamPendingOptions options, RespireKey key,
        string group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var count = options.Count ?? StreamPendingOptions.DefaultCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count, nameof(options.Count));
        var idle = options.GetMinIdleMilliseconds();
        var args = new RespireValue[5 + (idle.HasValue ? 2 : 0) + (options.Consumer is null ? 0 : 1)];
        var index = 0;
        args[index++] = client.Key(in key);
        args[index++] = group;
        if (idle is { } milliseconds)
        {
            args[index++] = "IDLE";
            args[index++] = milliseconds;
        }
        args[index++] = (options.Start ?? RespireStreamId.Min).Value;
        args[index++] = (options.End ?? RespireStreamId.Max).Value;
        args[index++] = count;
        if (options.Consumer is { } consumer) args[index] = consumer;
        return client.ConvertResponseAsync("XPENDING", new CmdN(XPending, args), cancellationToken, this,
            static (StreamCommands _, in RespValue value) => ParsePendingEntries(in value));
    }

    public ValueTask<RespireStreamEntry[]> ClaimAsync(StreamClaimOptions options, RespireKey key, string group,
        string consumer, TimeSpan minIdle, params ReadOnlySpan<RespireStreamId> ids)
        => ClaimAsync(options, key, group, consumer, minIdle, ids, CancellationToken.None);

    public ValueTask<RespireStreamEntry[]> ClaimAsync(StreamClaimOptions options, RespireKey key, string group,
        string consumer, TimeSpan minIdle, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
    {
        var resolvedKey = client.Key(in key);
        var command = BuildClaimCommand(options, resolvedKey, group, consumer, minIdle, ids, justIds: false);
        return ClaimCoreAsync(command, resolvedKey, group, cancellationToken);
    }

    public ValueTask<RespireStreamId[]> ClaimIdsAsync(RespireKey key, string group, string consumer,
        TimeSpan minIdle, params ReadOnlySpan<RespireStreamId> ids)
        => ClaimIdsAsync(default, key, group, consumer, minIdle, ids, CancellationToken.None);

    public ValueTask<RespireStreamId[]> ClaimIdsAsync(RespireKey key, string group, string consumer,
        TimeSpan minIdle, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
        => ClaimIdsAsync(default, key, group, consumer, minIdle, ids, cancellationToken);

    public ValueTask<RespireStreamId[]> ClaimIdsAsync(StreamClaimOptions options, RespireKey key, string group,
        string consumer, TimeSpan minIdle, params ReadOnlySpan<RespireStreamId> ids)
        => ClaimIdsAsync(options, key, group, consumer, minIdle, ids, CancellationToken.None);

    public ValueTask<RespireStreamId[]> ClaimIdsAsync(StreamClaimOptions options, RespireKey key, string group,
        string consumer, TimeSpan minIdle, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
        => client.ConvertResponseAsync("XCLAIM",
            BuildClaimCommand(options, client.Key(in key), group, consumer, minIdle, ids, justIds: true),
            cancellationToken, this, static (StreamCommands _, in RespValue value) => ParseStreamIds(in value));

    private static Cmd1N BuildClaimCommand(StreamClaimOptions options, RespireValue key, string group,
        string consumer, TimeSpan minIdle, ReadOnlySpan<RespireStreamId> ids, bool justIds)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(consumer);
        RequireIds(ids);
        var minimum = ToMinimumIdleMilliseconds(minIdle);
        if (options.IdleTime.HasValue && options.DeliveryTime.HasValue)
            throw new ArgumentException("IDLE and TIME cannot be combined.", nameof(options));
        long? idle = options.IdleTime is { } duration ? ToMilliseconds(duration, nameof(options.IdleTime)) : null;
        if (options.RetryCount is { } retries) ArgumentOutOfRangeException.ThrowIfNegative(retries, nameof(options.RetryCount));
        long? delivery = options.DeliveryTime?.ToUnixTimeMilliseconds();
        if (delivery is { } timestamp) ArgumentOutOfRangeException.ThrowIfNegative(timestamp, nameof(options.DeliveryTime));
        var args = new RespireValue[3 + ids.Length + (idle.HasValue ? 2 : 0) + (delivery.HasValue ? 2 : 0)
            + (options.RetryCount.HasValue ? 2 : 0) + (options.Force ? 1 : 0) + (justIds ? 1 : 0)
            + (options.LastId.HasValue ? 2 : 0)];
        args[0] = group;
        args[1] = consumer;
        args[2] = minimum;
        var index = 3;
        foreach (var id in ids) args[index++] = id.Value;
        if (idle is { } milliseconds)
        {
            args[index++] = "IDLE";
            args[index++] = milliseconds;
        }
        if (delivery is { } time)
        {
            args[index++] = "TIME";
            args[index++] = time;
        }
        if (options.RetryCount is { } retry)
        {
            args[index++] = "RETRYCOUNT";
            args[index++] = retry;
        }
        if (options.Force) args[index++] = "FORCE";
        if (justIds) args[index++] = "JUSTID";
        if (options.LastId is { } last)
        {
            args[index++] = "LASTID";
            args[index] = last.Value;
        }
        return new Cmd1N(XClaim, key, args);
    }

    public ValueTask<RespireStreamClaimIdsResult> ClaimPendingIdsAsync(RespireKey key, string group, string consumer,
        TimeSpan minIdle, RespireStreamId? start = null, int count = 100, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("XAUTOCLAIM",
            BuildAutoClaimCommand(client.Key(in key), group, consumer, minIdle, start, count, justIds: true), cancellationToken, this,
            static (StreamCommands _, in RespValue value) =>
            {
                var values = value.AsArray();
                if (values.Length is not (2 or 3))
                    throw new RespireProtocolException("XAUTOCLAIM JUSTID requires a two- or three-element reply.");
                // Redis 7+ adds deleted pending IDs as the third element; Redis 6.2 returns two.
                return new RespireStreamClaimIdsResult(new RespireStreamId(values[0].AsString()),
                    ParseStreamIds(in values[1]), values.Length == 3 ? ParseStreamIds(in values[2]) : []);
            });

    private static CmdN BuildAutoClaimCommand(RespireValue key, string group, string consumer,
        TimeSpan minIdle, RespireStreamId? start, int count, bool justIds)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var args = new RespireValue[justIds ? 8 : 7];
        args[0] = key;
        args[1] = group;
        args[2] = consumer;
        args[3] = ToMinimumIdleMilliseconds(minIdle);
        args[4] = (start ?? RespireStreamId.Beginning).Value;
        args[5] = "COUNT";
        args[6] = count;
        if (justIds) args[7] = "JUSTID";
        return new CmdN(XAutoClaim, args);
    }

    private static long ToMinimumIdleMilliseconds(TimeSpan minIdle)
    {
        if (minIdle < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minIdle), "Minimum idle time must be non-negative.");
        return CeilingMilliseconds(minIdle);
    }
}
