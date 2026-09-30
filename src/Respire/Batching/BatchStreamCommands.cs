using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>Non-blocking stream commands queued by batches and transactions.</summary>
/// <remarks>Mirrors append, count, range, remove, trim, and acknowledge operations from
/// IStreamCommands. Blocking reads, consumer loops, and group administration remain client-only.</remarks>
public interface IBatchStreamCommands
{
    /// <summary>Appends fields with an auto-generated id. Redis: XADD.</summary>
    RespirePending<RespireStreamId> Add(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields);
    /// <summary>Appends with id/trimming options; null when NOMKSTREAM skips an absent stream. Redis: XADD.</summary>
    RespirePending<RespireStreamId?> Add(RespireKey key, StreamAddOptions options,
        params ReadOnlySpan<(string Field, RespireValue Value)> fields);
    /// <summary>Number of entries. Redis: XLEN.</summary>
    RespirePending<long> Count(RespireKey key);
    /// <summary>Owned entries in an inclusive id range, optionally newest first. Redis: XRANGE or XREVRANGE.</summary>
    RespirePending<RespireStreamEntry[]> Range(RespireKey key, RespireStreamId? start = null,
        RespireStreamId? end = null, int? count = null, bool descending = false);
    /// <summary>Removes entries; returns the number removed. Redis: XDEL.</summary>
    RespirePending<long> Remove(RespireKey key, params ReadOnlySpan<RespireStreamId> ids);
    /// <summary>Trims by maximum length; returns the number removed. Redis: XTRIM MAXLEN.</summary>
    RespirePending<long> TrimByMaxLength(RespireKey key, long maxLength, bool approximate = false);
    /// <summary>Acknowledges pending group entries; returns the number newly acknowledged. Redis: XACK.</summary>
    RespirePending<long> Acknowledge(RespireKey key, string group, params ReadOnlySpan<RespireStreamId> ids);
}

internal sealed class BatchStreamCommands(IPendingSink sink) : IBatchStreamCommands
{
    public RespirePending<RespireStreamId> Add(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => sink.Add<Cmd1N, RespireStreamId>("XADD",
            StreamCommands.BuildAddCommand(sink.Client, SnapshotKey(key), default, fields, snapshotValues: sink.DefersSerialization),
            static (_, value) => new RespireStreamId(ResponseReader.String(in value)));

    public RespirePending<RespireStreamId?> Add(RespireKey key, StreamAddOptions options,
        params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => sink.Add<Cmd1N, RespireStreamId?>("XADD",
            StreamCommands.BuildAddCommand(sink.Client, SnapshotKey(key), options, fields, snapshotValues: sink.DefersSerialization),
            static (_, value) => value.IsNull ? default(RespireStreamId?) : new RespireStreamId(ResponseReader.String(in value)));

    public RespirePending<long> Count(RespireKey key)
        => sink.Add<Cmd1, long>("XLEN", new Cmd1(Verbs.XLen, sink.Client.Key(SnapshotKey(key))),
            static (_, value) => ResponseReader.Integer(in value));

    public RespirePending<RespireStreamEntry[]> Range(RespireKey key, RespireStreamId? start = null,
        RespireStreamId? end = null, int? count = null, bool descending = false)
    {
        var (from, to) = StreamCommands.RangeBounds(start, end, descending);
        var operation = descending ? "XREVRANGE" : "XRANGE";
        var verb = descending ? Verbs.XRevRange : Verbs.XRange;
        // Range entries own their fields and do not carry a consumer-group acknowledgement context.
        return count is { } take
            ? sink.Add<Cmd5, RespireStreamEntry[]>(operation, new Cmd5(verb, sink.Client.Key(SnapshotKey(key)), from, to, "COUNT", take),
                static (_, value) => StreamCommands.ParseEntries(in value, client: null, resolvedKey: default, group: null))
            : sink.Add<Cmd3, RespireStreamEntry[]>(operation, new Cmd3(verb, sink.Client.Key(SnapshotKey(key)), from, to),
                static (_, value) => StreamCommands.ParseEntries(in value, client: null, resolvedKey: default, group: null));
    }

    public RespirePending<long> Remove(RespireKey key, params ReadOnlySpan<RespireStreamId> ids)
        => sink.Add<Cmd1N, long>("XDEL", StreamCommands.BuildRemoveCommand(sink.Client, SnapshotKey(key), ids),
            static (_, value) => ResponseReader.Integer(in value));

    public RespirePending<long> TrimByMaxLength(RespireKey key, long maxLength, bool approximate = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        return approximate
            ? sink.Add<Cmd4, long>("XTRIM", new Cmd4(StreamCommands.XTrim, sink.Client.Key(SnapshotKey(key)), "MAXLEN", "~", maxLength),
                static (_, value) => ResponseReader.Integer(in value))
            : sink.Add<Cmd3, long>("XTRIM", new Cmd3(StreamCommands.XTrim, sink.Client.Key(SnapshotKey(key)), "MAXLEN", maxLength),
                static (_, value) => ResponseReader.Integer(in value));
    }

    public RespirePending<long> Acknowledge(RespireKey key, string group, params ReadOnlySpan<RespireStreamId> ids)
        => sink.Add<Cmd2N, long>("XACK", StreamCommands.BuildAcknowledgeCommand(sink.Client, SnapshotKey(key), group, ids),
            static (_, value) => ResponseReader.Integer(in value));

    // Transactions serialize during Add; batches retain the command until ExecuteAsync.
    private RespireKey SnapshotKey(RespireKey key) => sink.DefersSerialization ? key.Snapshot() : key;
}
