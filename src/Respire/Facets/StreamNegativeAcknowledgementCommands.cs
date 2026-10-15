using Respire.Commands;
using Respire.Protocol;

namespace Respire;

/// <summary>How XNACK adjusts the pending entry's delivery count. Redis 8.8+.</summary>
public enum RespireStreamNackMode
{
    /// <summary>Decrements the delivery count, stopping at zero (SILENT).</summary>
    Silent,
    /// <summary>Preserves the delivery count (FAIL).</summary>
    Fail,
    /// <summary>Sets the delivery count to Int64.MaxValue (FATAL).</summary>
    Fatal,
}

/// <summary>Advanced XNACK controls. Defaults preserve the selected mode and affect existing pending entries only.</summary>
public readonly record struct RespireStreamNackOptions
{
    /// <summary>Overrides the mode's delivery count adjustment. Must be nonnegative.</summary>
    public long? RetryCount { get; init; }
    /// <summary>Creates unowned pending entries for existing stream IDs absent from the group's PEL.</summary>
    public bool Force { get; init; }
}

public partial interface IRespireStreamCommands
{
    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode, RespireStreamId ids, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode, RespireStreamNackOptions options, RespireStreamId ids, CancellationToken cancellationToken);

    /// <summary>Releases pending entries from their consumers. Returns Redis's aggregate count, not per-ID outcomes. Redis 8.8+: XNACK.</summary>
    ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode,
        params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Releases pending entries with cancellation. Redis 8.8+: XNACK.</summary>
    ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode,
        ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);

    /// <summary>Releases pending entries with advanced counter/force options. Redis 8.8+: XNACK.</summary>
    ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode,
        RespireStreamNackOptions options, params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Releases pending entries with advanced counter/force options. Cancellation cannot undo a dispatched write. Redis 8.8+: XNACK.</summary>
    ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode,
        RespireStreamNackOptions options, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);
}

internal sealed partial class StreamCommands
{
    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode, RespireStreamId ids, CancellationToken cancellationToken)
        => NegativeAcknowledgeAsync(key, group, mode, [ids], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode, RespireStreamNackOptions options, RespireStreamId ids, CancellationToken cancellationToken)
        => NegativeAcknowledgeAsync(key, group, mode, options, [ids], cancellationToken);


    private static readonly Verb XNack = new("XNACK");

    public ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode,
        params ReadOnlySpan<RespireStreamId> ids)
        => NegativeAcknowledgeAsync(key, group, mode, default, ids, CancellationToken.None);

    public ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode,
        ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
        => NegativeAcknowledgeAsync(key, group, mode, default, ids, cancellationToken);

    public ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode,
        RespireStreamNackOptions options, params ReadOnlySpan<RespireStreamId> ids)
        => NegativeAcknowledgeAsync(key, group, mode, options, ids, CancellationToken.None);

    public ValueTask<long> NegativeAcknowledgeAsync(RespireKey key, RespireValue group, RespireStreamNackMode mode,
        RespireStreamNackOptions options, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
    {
        if (client.Core.Options.CommandHook is null) cancellationToken.ThrowIfCancellationRequested();
        return client.ConvertResponseAsync("XNACK", BuildNackCommand(client, key, group, mode, options, ids),
            cancellationToken, this, static (StreamCommands _, in RespValue value) => ParseNackCount(in value));
    }

    internal static Cmd1N BuildNackCommand(RespireClient client, RespireKey key, RespireValue group,
        RespireStreamNackMode mode, RespireStreamNackOptions options, ReadOnlySpan<RespireStreamId> ids)
    {
        RespireValue.ThrowIfNull(group, nameof(group));
        RequireIds(ids);
        if (options.RetryCount is < 0) throw new ArgumentOutOfRangeException(nameof(options));
        var token = mode switch
        {
            RespireStreamNackMode.Silent => "SILENT",
            RespireStreamNackMode.Fail => "FAIL",
            RespireStreamNackMode.Fatal => "FATAL",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        ValidateNumericIds(ids);
        var arguments = new RespireValue[4 + ids.Length + (options.RetryCount.HasValue ? 2 : 0) + (options.Force ? 1 : 0)];
        arguments[0] = group.Snapshot();
        arguments[1] = token;
        arguments[2] = "IDS";
        arguments[3] = ids.Length;
        var index = 4;
        foreach (var id in ids) arguments[index++] = id.Value;
        if (options.RetryCount is { } count)
        {
            arguments[index++] = "RETRYCOUNT";
            arguments[index++] = count;
        }
        if (options.Force) arguments[index] = "FORCE";
        return new(XNack, client.Key(key), arguments);
    }

    internal static long ParseNackCount(in RespValue value)
    {
        if (value.Type != RespireDataType.Integer || value.AsInteger() < 0)
            throw new RespireProtocolException("XNACK must return a nonnegative integer count.");
        return value.AsInteger();
    }
}
