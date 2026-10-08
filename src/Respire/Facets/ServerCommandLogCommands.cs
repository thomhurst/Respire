using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>The Valkey 8.1+ command log to inspect. Each type has independent limits and IDs.</summary>
public enum RespireCommandLogType
{
    /// <summary>Execution time, excluding client I/O, in microseconds.</summary>
    Slow,
    /// <summary>Request size in bytes.</summary>
    LargeRequest,
    /// <summary>Reply size in bytes.</summary>
    LargeReply,
}

/// <summary>An owned COMMANDLOG entry. Arguments and client metadata preserve the server's bytes.</summary>
/// <remarks>MetricValue is microseconds for Slow and bytes otherwise. AdditionalValues own future trailing fields;
/// disposing those GC-owned results is optional. Server-side truncation and redaction cannot be reversed.</remarks>
public sealed record RespireCommandLogEntry(
    RespireCommandLogType Type, long Id, long TimestampUnixSeconds, long MetricValue,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[][] Arguments,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[] ClientAddress,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[] ClientName,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireResult[] AdditionalValues)
{
    /// <summary>Execution microseconds for Slow entries; null for size logs.</summary>
    public long? DurationMicroseconds => Type == RespireCommandLogType.Slow ? MetricValue : null;
    /// <summary>Request bytes for LargeRequest entries; null otherwise.</summary>
    public long? RequestBytes => Type == RespireCommandLogType.LargeRequest ? MetricValue : null;
    /// <summary>Reply bytes for LargeReply entries; null otherwise.</summary>
    public long? ReplyBytes => Type == RespireCommandLogType.LargeReply ? MetricValue : null;
}

public partial interface IServerCommands
{
    /// <summary>Returns owned Valkey 8.1+ command log entries in server order. Count -1 means all, 0 means none.</summary>
    ValueTask<RespireCommandLogEntry[]> CommandLogAsync(RespireCommandLogType type, long count = 10, CancellationToken cancellationToken = default);
    /// <summary>Returns one node's current log length. Valkey 8.1+: COMMANDLOG LEN.</summary>
    ValueTask<long> CommandLogLengthAsync(RespireCommandLogType type, CancellationToken cancellationToken = default);
    /// <summary>Clears one node's selected log; IDs are not reset. Requires AllowAdmin. Valkey 8.1+.</summary>
    ValueTask ResetCommandLogAsync(RespireCommandLogType type, CancellationToken cancellationToken = default);
    /// <summary>Returns owned logs separately from every discovered node, including replicas, with endpoint provenance.</summary>
    /// <remarks>Discovery cancellation throws. Later cancellation and failures are per-node results; standalone returns one result.</remarks>
    ValueTask<RespireServerResult<RespireCommandLogEntry[]>[]> CommandLogOnAllNodesAsync(RespireCommandLogType type, long count = 10, CancellationToken cancellationToken = default);
    /// <summary>Returns each discovered node's log length. Failures remain endpoint-associated results.</summary>
    ValueTask<RespireServerResult<long>[]> CommandLogLengthOnAllNodesAsync(RespireCommandLogType type, CancellationToken cancellationToken = default);
    /// <summary>Explicitly clears every discovered node's selected log, including replicas and slotless members. Requires AllowAdmin; true means OK on that node.</summary>
    /// <remarks>Partial success is possible; resets are neither replicated nor rolled back across nodes.</remarks>
    ValueTask<RespireServerResult<bool>[]> ResetCommandLogOnAllNodesAsync(RespireCommandLogType type, CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    private static readonly Verb CommandLogGet = new(-1, "COMMANDLOG", "GET");
    private static readonly Verb CommandLogLen = new(-1, "COMMANDLOG", "LEN");
    private static readonly Verb CommandLogReset = new(-1, "COMMANDLOG", "RESET");

    public ValueTask<RespireCommandLogEntry[]> CommandLogAsync(RespireCommandLogType type, long count = 10, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ConvertAsync("COMMANDLOG GET", BuildCommandLogGet(type, count), cancellationToken, CommandLogConverter(type));
    }

    public ValueTask<long> CommandLogLengthAsync(RespireCommandLogType type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ConvertAsync("COMMANDLOG LEN", new Cmd1(CommandLogLen, CommandLogTypeToken(type)), cancellationToken,
            static (ServerCommands _, in RespValue value) => CommandLogParser.NonnegativeInteger(in value));
    }

    public ValueTask ResetCommandLogAsync(RespireCommandLogType type, CancellationToken cancellationToken = default)
    {
        EnsureAdminAllowed("COMMANDLOG RESET");
        cancellationToken.ThrowIfCancellationRequested();
        return DiscardCommandLogResetAsync(ConvertAsync("COMMANDLOG RESET", new Cmd1(CommandLogReset, CommandLogTypeToken(type)),
            cancellationToken, static (ServerCommands _, in RespValue value) => CommandLogParser.Ok(in value)));
    }

    public ValueTask<RespireServerResult<RespireCommandLogEntry[]>[]> CommandLogOnAllNodesAsync(RespireCommandLogType type, long count = 10, CancellationToken cancellationToken = default)
        => FanOutAsync("COMMANDLOG GET", BuildCommandLogGet(type, count), cancellationToken, CommandLogConverter(type));

    public ValueTask<RespireServerResult<long>[]> CommandLogLengthOnAllNodesAsync(RespireCommandLogType type, CancellationToken cancellationToken = default)
        => FanOutAsync("COMMANDLOG LEN", new Cmd1(CommandLogLen, CommandLogTypeToken(type)), cancellationToken,
            static (ServerCommands _, in RespValue value) => CommandLogParser.NonnegativeInteger(in value));

    public async ValueTask<RespireServerResult<bool>[]> ResetCommandLogOnAllNodesAsync(RespireCommandLogType type, CancellationToken cancellationToken = default)
    {
        EnsureAdminAllowed("COMMANDLOG RESET");
        cancellationToken.ThrowIfCancellationRequested();
        var command = new Cmd1(CommandLogReset, CommandLogTypeToken(type));
        var cache = client.Core.ClientCache;
        var fence = cache is null ? default : cache.BeforeCommand("COMMANDLOG RESET", command);
        try
        {
            return await FanOutAsync("COMMANDLOG RESET", new MutationCommand<Cmd1>(command, fence), cancellationToken,
                static (ServerCommands _, in RespValue value) => CommandLogParser.Ok(in value)).ConfigureAwait(false);
        }
        finally
        {
            if (fence.IsRequired) cache!.CompleteMutation(in fence);
        }
    }

    private static async ValueTask DiscardCommandLogResetAsync(ValueTask<bool> operation) => _ = await operation.ConfigureAwait(false);

    private static Cmd2 BuildCommandLogGet(RespireCommandLogType type, long count)
    {
        if (count < -1) throw new ArgumentOutOfRangeException(nameof(count), "Count must be at least -1; -1 requests all entries.");
        return new(CommandLogGet, count, CommandLogTypeToken(type));
    }

    private static string CommandLogTypeToken(RespireCommandLogType type) => type switch
    {
        RespireCommandLogType.Slow => "SLOW",
        RespireCommandLogType.LargeRequest => "LARGE-REQUEST",
        RespireCommandLogType.LargeReply => "LARGE-REPLY",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    // Static delegates retain each log type without allocating a capturing closure.
    private static ResponseConverter<ServerCommands, RespireCommandLogEntry[]> CommandLogConverter(RespireCommandLogType type) => type switch
    {
        RespireCommandLogType.Slow => static (ServerCommands _, in RespValue value) => CommandLogParser.Parse(in value, RespireCommandLogType.Slow),
        RespireCommandLogType.LargeRequest => static (ServerCommands _, in RespValue value) => CommandLogParser.Parse(in value, RespireCommandLogType.LargeRequest),
        RespireCommandLogType.LargeReply => static (ServerCommands _, in RespValue value) => CommandLogParser.Parse(in value, RespireCommandLogType.LargeReply),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
