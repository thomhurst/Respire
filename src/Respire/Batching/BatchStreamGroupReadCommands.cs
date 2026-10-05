using Respire.Commands;

namespace Respire;

public partial interface IBatchStreamCommands
{
    /// <summary>Queues one nonblocking consumer-group page. Null startAt reads new entries.</summary>
    RespirePending<RespireStreamEntry[]> ReadGroup(RespireKey key, string group, string consumer,
        StreamReadOptions options = default, RespireStreamId? startAt = null);

    /// <summary>Queues one nonblocking page from same-slot group streams. Use &gt; for new entries.</summary>
    RespirePending<RespireStreamReadResult[]> ReadGroup(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        string group, string consumer, StreamReadOptions options = default);
}

internal sealed partial class BatchStreamCommands
{
    public RespirePending<RespireStreamEntry[]> ReadGroup(RespireKey key, string group, string consumer,
        StreamReadOptions options = default, RespireStreamId? startAt = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(consumer);
        return sink.Add<StreamReadCommand, RespireStreamEntry[]>("XREADGROUP",
            StreamCommands.BuildReadCommand(sink.Client, [(key, startAt ?? (RespireStreamId)">")], options,
                queued: true, group: group, consumer: consumer),
            (client, value) =>
            {
                var result = StreamCommands.ParseStreamRead(in value, client, group);
                return result.Length == 0 ? [] : result[0].Entries;
            });
    }

    public RespirePending<RespireStreamReadResult[]> ReadGroup(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        string group, string consumer, StreamReadOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(consumer);
        return sink.Add<StreamReadCommand, RespireStreamReadResult[]>("XREADGROUP",
            StreamCommands.BuildReadCommand(sink.Client, streams, options, queued: true, group: group, consumer: consumer),
            (client, value) => StreamCommands.ParseStreamRead(in value, client, group));
    }
}
