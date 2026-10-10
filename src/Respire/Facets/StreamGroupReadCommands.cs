using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Protocol;

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
    public ValueTask<RespireStreamEntry[]> ReadGroupOnceAsync(RespireKey key, string group, string consumer,
        StreamReadOptions options = default, RespireStreamId? startAt = null, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireStreamEntry[]>.Start();
        try
        {
            return owner.Attach(ReadGroupSingleBorrowedAsync(key, group, consumer, options, startAt,
                cancellationToken, owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireStreamEntry[]> ReadGroupSingleBorrowedAsync(RespireKey key, string group, string consumer,
        StreamReadOptions options, RespireStreamId? startAt, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        var result = await ReadGroupBorrowedAsync([(key, startAt ?? (RespireStreamId)">")],
            group, consumer, options, cancellationToken, observation).ConfigureAwait(false);
        return result.Length == 0 ? [] : result[0].Entries;
    }

    public ValueTask<RespireStreamReadResult[]> ReadGroupAsync(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        string group, string consumer, StreamReadOptions options = default, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireStreamReadResult[]>.Start();
        try
        {
            return owner.Attach(ReadGroupBorrowedAsync(streams, group, consumer, options, cancellationToken, owner.Observation));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireStreamReadResult[]> ReadGroupBorrowedAsync(
        ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams, string group, string consumer,
        StreamReadOptions options, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(consumer);
        var command = BuildReadCommand(client, streams, options, group: group, consumer: consumer);
        return ReadGroupCoreAsync(command, group, options.WaitFor.HasValue, cancellationToken, observation);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireStreamReadResult[]> ReadGroupCoreAsync(Commands.StreamReadCommand command,
        string group, bool blocking, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        var state = (Client: client, Group: group);
        return await (blocking
            ? client.ConvertBlockingResponseAsync("XREADGROUP", command, cancellationToken, state,
                static ((RespireClient Client, string Group) owner, in RespValue reply) => ParseStreamRead(in reply, owner.Client, owner.Group), observation)
            : client.ConvertResponseAsync("XREADGROUP", command, cancellationToken, state,
                static ((RespireClient Client, string Group) owner, in RespValue reply) => ParseStreamRead(in reply, owner.Client, owner.Group), observation: observation))
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RespireStreamEntry> ReadGroupAsync(StreamReadOptions options, RespireKey key,
        string group, string consumer, RespireStreamId? startAt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        Exception? failure = null;
        try
        {
            try
            {
                options.Validate(group: true);
                key = key.Snapshot();
                options = options with { WaitFor = startAt is null ? options.WaitFor ?? BlockInterval : null };
            }
            catch (Exception error) { failure = error; throw; }
            var cursor = startAt;
            while (true)
            {
                RespireStreamReadResult[] results;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results = await ReadGroupBorrowedAsync([(key, cursor ?? (RespireStreamId)">")],
                        group, consumer, options, cancellationToken, owner.Observation).ConfigureAwait(false);
                }
                catch (Exception error) { failure = error; throw; }
                var entries = results.Length == 0 ? [] : results[0].Entries;
                if (entries.Length == 0 && startAt is not null) yield break;
                foreach (var entry in entries)
                {
                    try { cancellationToken.ThrowIfCancellationRequested(); }
                    catch (Exception error) { failure = error; throw; }
                    yield return entry;
                }
                if (startAt is not null && entries.Length != 0) cursor = entries[^1].Id;
            }
        }
        finally { FinishReadIterator(owner, failure); }
    }
}
