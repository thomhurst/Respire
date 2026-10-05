using Respire.Commands;

namespace Respire;

public partial interface IBatchStreamCommands
{
    /// <summary>Queues a nonblocking read with optional Redis 8.10 cumulative reply limits.</summary>
    RespirePending<RespireStreamEntry[]> Read(StreamReadOptions options, RespireKey key, RespireStreamId after = default);

    /// <summary>Queues a same-slot read with shared reply limits. WaitFor must be null.</summary>
    RespirePending<RespireStreamReadResult[]> Read(StreamReadOptions options, ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams);

    /// <summary>Queues nonblocking XREAD for one stream. Returns owned entries newer than after.</summary>
    RespirePending<RespireStreamEntry[]> Read(RespireKey key, RespireStreamId after = default, int? count = null);

    /// <summary>Queues nonblocking XREAD for same-slot streams. COUNT applies per stream; results own their data.</summary>
    RespirePending<RespireStreamReadResult[]> Read(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams, int? count = null);
}

internal sealed partial class BatchStreamCommands
{
    public RespirePending<RespireStreamEntry[]> Read(StreamReadOptions options, RespireKey key, RespireStreamId after = default)
        => sink.Add<StreamReadCommand, RespireStreamEntry[]>("XREAD",
            StreamCommands.BuildReadCommand(sink.Client, [(key, after)], options, queued: true),
            static (client, value) =>
            {
                var result = StreamCommands.ParseStreamRead(in value, client);
                return result.Length == 0 ? [] : result[0].Entries;
            });

    public RespirePending<RespireStreamReadResult[]> Read(StreamReadOptions options, ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams)
        => sink.Add<StreamReadCommand, RespireStreamReadResult[]>("XREAD",
            StreamCommands.BuildReadCommand(sink.Client, streams, options, queued: true),
            static (client, value) => StreamCommands.ParseStreamRead(in value, client));

    public RespirePending<RespireStreamEntry[]> Read(RespireKey key, RespireStreamId after = default, int? count = null)
        => sink.Add<StreamReadCommand, RespireStreamEntry[]>("XREAD",
            StreamCommands.BuildReadCommand(sink.Client, [(key, after)], count, waitFor: null),
            static (client, value) =>
            {
                var result = StreamCommands.ParseStreamRead(in value, client);
                return result.Length == 0 ? [] : result[0].Entries;
            });

    public RespirePending<RespireStreamReadResult[]> Read(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams, int? count = null)
        => sink.Add<StreamReadCommand, RespireStreamReadResult[]>("XREAD",
            StreamCommands.BuildReadCommand(sink.Client, streams, count, waitFor: null),
            static (client, value) => StreamCommands.ParseStreamRead(in value, client));
}
