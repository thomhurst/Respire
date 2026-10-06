using System.Runtime.CompilerServices;

namespace Respire;

public partial interface IStreamCommands
{
    /// <summary>Reads one consumer-group page. Null startAt reads new entries; a numeric ID reads pending entries.</summary>
    ValueTask<RespireStreamEntry[]> ReadGroupOnceAsync(RespireKey key, string group, string consumer,
        StreamReadOptions options = default, RespireStreamId? startAt = null, CancellationToken cancellationToken = default);

    /// <summary>Reads one page from same-slot consumer-group streams. Use &gt; for new entries or numeric pending cursors.</summary>
    ValueTask<RespireStreamReadResult[]> ReadGroupAsync(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        string group, string consumer, StreamReadOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Continuously reads new group entries, or replays pending entries until empty, with reply limits.</summary>
    /// <remarks>New-entry reads use a five-second blocking interval when WaitFor is null. Pending reads do not block.</remarks>
    IAsyncEnumerable<RespireStreamEntry> ReadGroupAsync(StreamReadOptions options, RespireKey key, string group,
        string consumer, RespireStreamId? startAt = null, CancellationToken cancellationToken = default);
}

internal sealed partial class StreamCommands
{
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<RespireStreamEntry[]> ReadGroupOnceAsync(RespireKey key, string group, string consumer,
        StreamReadOptions options = default, RespireStreamId? startAt = null, CancellationToken cancellationToken = default)
    {
        var result = await ReadGroupAsync([(key, startAt ?? (RespireStreamId)">")], group, consumer, options, cancellationToken)
            .ConfigureAwait(false);
        return result.Length == 0 ? [] : result[0].Entries;
    }

    public ValueTask<RespireStreamReadResult[]> ReadGroupAsync(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        string group, string consumer, StreamReadOptions options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(consumer);
        var command = BuildReadCommand(client, streams, options, group: group, consumer: consumer);
        return ReadGroupCoreAsync(command, group, options.WaitFor.HasValue, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireStreamReadResult[]> ReadGroupCoreAsync(Commands.StreamReadCommand command,
        string group, bool blocking, CancellationToken cancellationToken)
    {
        using var reply = blocking
            ? await client.SendBlockingAsync("XREADGROUP", command, cancellationToken).ConfigureAwait(false)
            : await client.SendAsync("XREADGROUP", command, cancellationToken).ConfigureAwait(false);
        return ParseStreamRead(in reply, client, group);
    }

    public async IAsyncEnumerable<RespireStreamEntry> ReadGroupAsync(StreamReadOptions options, RespireKey key,
        string group, string consumer, RespireStreamId? startAt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.Validate(group: true);
        key = key.Snapshot();
        options = options with { WaitFor = startAt is null ? options.WaitFor ?? BlockInterval : null };
        var cursor = startAt;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await ReadGroupOnceAsync(key, group, consumer, options, cursor, cancellationToken).ConfigureAwait(false);
            if (entries.Length == 0 && startAt is not null) yield break;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
            if (startAt is not null && entries.Length != 0) cursor = entries[^1].Id;
        }
    }
}
