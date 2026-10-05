using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>Owned entries returned for one stream by XREAD. Key has the client's prefix removed.</summary>
public readonly record struct RespireStreamReadResult(RespireKey Key, RespireStreamEntry[] Entries);

/// <summary>One owned entry and its stream key, returned by a multi-stream enumeration.</summary>
public readonly record struct RespireStreamReadEntry(RespireKey Key, RespireStreamEntry Entry);

public partial interface IStreamCommands
{
    /// <summary>Reads one stream with optional cumulative Redis 8.10 reply limits.</summary>
    ValueTask<RespireStreamEntry[]> ReadAsync(StreamReadOptions options, RespireKey key,
        RespireStreamId after = default, CancellationToken cancellationToken = default);

    /// <summary>Reads same-slot streams with shared reply limits. Options precede keys to preserve existing overloads.</summary>
    ValueTask<RespireStreamReadResult[]> ReadAsync(StreamReadOptions options,
        ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams, CancellationToken cancellationToken = default);

    /// <summary>Reads entries newer than after. Null waitFor is nonblocking; InfiniteTimeSpan waits until cancelled.</summary>
    ValueTask<RespireStreamEntry[]> ReadAsync(RespireKey key, RespireStreamId after = default,
        int? count = null, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Reads newer entries from same-slot streams. COUNT applies per stream. Empty/timeout replies return an empty array.</summary>
    ValueTask<RespireStreamReadResult[]> ReadAsync(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        int? count = null, TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>Continuously reads a stream, resuming after the last delivered id following transient connection failures.</summary>
    /// <remarks>$ is resolved once before reading. Failure to resolve the initial position is surfaced without retry.
    /// Transient failures retry until cancellation/disposal, with backoff capped at 3.2 seconds.
    /// Trimming/deletion during an outage can remove entries. Disposal/cancellation ends the read.</remarks>
    IAsyncEnumerable<RespireStreamEntry> ReadAllAsync(RespireKey key, RespireStreamId after = default,
        int batchSize = 64, CancellationToken cancellationToken = default);

    /// <summary>Continuously reads same-slot streams with an independent last-delivered cursor for each stream.</summary>
    /// <remarks>Keys are copied when called. $ positions are resolved once per stream before reading; resolution failure
    /// is surfaced without retry. Buffered entries are delivered before another read or reconnect attempt.
    /// Transient failures retry until cancellation/disposal, with backoff capped at 3.2 seconds.</remarks>
    IAsyncEnumerable<RespireStreamReadEntry> ReadAllAsync(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        int batchSize = 64, CancellationToken cancellationToken = default);
}

internal sealed partial class StreamCommands
{
    public ValueTask<RespireStreamEntry[]> ReadAsync(StreamReadOptions options, RespireKey key,
        RespireStreamId after = default, CancellationToken cancellationToken = default)
        => ReadSingleAsync(BuildReadCommand(client, [(key, after)], options), options.WaitFor.HasValue, cancellationToken);

    public ValueTask<RespireStreamReadResult[]> ReadAsync(StreamReadOptions options,
        ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams, CancellationToken cancellationToken = default)
        => ReadCoreAsync(BuildReadCommand(client, streams, options), options.WaitFor.HasValue, cancellationToken);

    public ValueTask<RespireStreamEntry[]> ReadAsync(RespireKey key, RespireStreamId after = default,
        int? count = null, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => ReadSingleAsync(BuildReadCommand(client, [(key, after)], count, waitFor), waitFor.HasValue, cancellationToken);

    public ValueTask<RespireStreamReadResult[]> ReadAsync(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        int? count = null, TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
        => ReadCoreAsync(BuildReadCommand(client, streams, count, waitFor), waitFor.HasValue, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireStreamEntry[]> ReadSingleAsync(StreamReadCommand command, bool blocking, CancellationToken cancellationToken)
    {
        var result = await ReadCoreAsync(command, blocking, cancellationToken).ConfigureAwait(false);
        return result.Length == 0 ? [] : result[0].Entries;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireStreamReadResult[]> ReadCoreAsync(StreamReadCommand command, bool blocking, CancellationToken cancellationToken)
    {
        using var reply = blocking
            ? await client.SendBlockingAsync("XREAD", command, cancellationToken).ConfigureAwait(false)
            : await client.SendAsync("XREAD", command, cancellationToken).ConfigureAwait(false);
        return ParseStreamRead(in reply, client);
    }

    internal static StreamReadCommand BuildReadCommand(RespireClient client,
        ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams, int? count, TimeSpan? waitFor)
        => BuildReadCommand(client, streams, new StreamReadOptions { Count = count, WaitFor = waitFor });

    internal static StreamReadCommand BuildReadCommand(RespireClient client,
        ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams, StreamReadOptions options,
        bool queued = false, string? group = null, string? consumer = null)
    {
        options.Validate(queued);
        var milliseconds = options.GetBlockMilliseconds();
        var snapshots = SnapshotStreams(client, streams, group is not null);
        var keys = new RespireValue[snapshots.Length];
        var ids = new RespireStreamId[snapshots.Length];
        for (var i = 0; i < snapshots.Length; i++)
        {
            keys[i] = client.Key(snapshots[i].Key);
            ids[i] = snapshots[i].After;
        }
        return new StreamReadCommand(keys, ids, options.Count, milliseconds, options.MaxCount, options.MaxSize, group, consumer);
    }

    private static (RespireKey Key, RespireStreamId After)[] SnapshotStreams(RespireClient client,
        ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams, bool group = false)
    {
        if (streams.IsEmpty) throw new ArgumentException("At least one stream is required.", nameof(streams));
        var snapshot = streams.ToArray();
        var seen = new HashSet<RespireKey>();
        int? slot = null;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var key = snapshot[i].Key.Snapshot();
            if (!seen.Add(key)) throw new ArgumentException("Each stream key must appear only once.", nameof(streams));
            var id = snapshot[i].After;
            if (group ? id.Value != ">" : id != RespireStreamId.New)
            {
                if (id == RespireStreamId.Min || id == RespireStreamId.Max || (group && id == RespireStreamId.New))
                    throw new ArgumentException(group ? "XREADGROUP requires a numeric start id or >." : "XREAD requires a numeric start id or $.", nameof(streams));
                try
                {
                    _ = id.CompareTo(RespireStreamId.Beginning);
                }
                catch (FormatException error)
                {
                    throw new ArgumentException(group ? "XREADGROUP requires a numeric start id or >." : "XREAD requires a numeric start id or $.", nameof(streams), error);
                }
            }
            if (client.Core.Cluster is not null)
            {
                var current = client.ResolveKey(key).ClusterSlot;
                if (slot is { } expected && current != expected)
                    throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", group ? "XREADGROUP" : "XREAD");
                slot = current;
            }
            snapshot[i] = (key, id);
        }
        return snapshot;
    }

    internal static RespireStreamReadResult[] ParseStreamRead(in RespValue reply, RespireClient client, string? group = null)
    {
        if (reply.IsNull) return [];
        if (reply.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("XREAD must return stream/entries pairs.");
        var values = reply.AsArray();
        var map = reply.Type == RespDataType.Map;
        if (map && values.Length % 2 != 0) throw new RespireProtocolException("XREAD returned an incomplete stream pair.");
        var result = new RespireStreamReadResult[map ? values.Length / 2 : values.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if (!map && values[i].Type != RespDataType.Array)
                throw new RespireProtocolException("XREAD returned an invalid stream pair.");
            var pair = map ? values.Slice(i * 2, 2) : values[i].AsArray();
            if (pair.Length != 2 || pair[0].Type is not (RespDataType.BulkString or RespDataType.SimpleString)
                || pair[1].Type != RespDataType.Array)
                throw new RespireProtocolException("XREAD returned an invalid stream pair.");
            var key = MultiKeyPop.ParsePoppedKey(in pair[0], client.KeyPrefixBytes);
            result[i] = new(key, ParseEntries(in pair[1], group is null ? null : client, group is null ? default : client.Key(key), group));
        }
        return result;
    }

    public IAsyncEnumerable<RespireStreamEntry> ReadAllAsync(RespireKey key, RespireStreamId after = default,
        int batchSize = 64, CancellationToken cancellationToken = default)
        => EntriesOnly(ReadAllAsync([(key, after)], batchSize, cancellationToken));

    private static async IAsyncEnumerable<RespireStreamEntry> EntriesOnly(IAsyncEnumerable<RespireStreamReadEntry> entries,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false)) yield return entry.Entry;
    }

    public IAsyncEnumerable<RespireStreamReadEntry> ReadAllAsync(ReadOnlySpan<(RespireKey Key, RespireStreamId After)> streams,
        int batchSize = 64, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        return ReadContinuouslyAsync(SnapshotStreams(client, streams), batchSize, cancellationToken);
    }

    private async IAsyncEnumerable<RespireStreamReadEntry> ReadContinuouslyAsync(
        (RespireKey Key, RespireStreamId After)[] initial, int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Each enumeration gets private cursors. Never re-evaluate $ after a transport failure.
        var streams = initial.ToArray();
        for (var i = 0; i < streams.Length; i++)
        {
            if (streams[i].After != RespireStreamId.New) continue;
            var newest = await RangeAsync(streams[i].Key, count: 1, descending: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            streams[i].After = newest.Length == 0 ? RespireStreamId.Beginning : newest[0].Id;
        }
        var indexes = streams.Select((stream, index) => (stream.Key, index)).ToDictionary(x => x.Key, x => x.index);
        var command = BuildReadCommand(client, streams, batchSize, TimeSpan.FromSeconds(1));
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RespireStreamReadResult[] results;
            try
            {
                results = await ReadCoreAsync(command, blocking: true, cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && !client.Core.Disposed && CanRetryStreamRead(error))
            {
                var delayMilliseconds = 100 * (1 << Math.Min(failures, 5));
                failures = Math.Min(failures + 1, 6);
                client.Core.Logger?.LogWarning(error, "Stream read failed; retrying after {DelayMilliseconds} ms.", delayMilliseconds);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken).ConfigureAwait(false);
                continue;
            }
            foreach (var result in results)
            {
                if (!indexes.TryGetValue(result.Key, out var index))
                    throw new RespireProtocolException("XREAD returned an unrequested stream.");
                foreach (var entry in result.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Id <= streams[index].After)
                        throw new RespireProtocolException("XREAD returned a non-increasing entry id.");
                    streams[index].After = entry.Id;
                    command.SetAfter(index, entry.Id);
                    yield return new RespireStreamReadEntry(result.Key, entry);
                }
            }
        }
    }

    private static bool CanRetryStreamRead(Exception error)
    {
        // Handshake errors retain the server error as their cause; ACL and authentication failures are terminal.
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is RespireServerException server) return server.IsTransient;
            if (cause is AuthenticationException or RespireConfigurationException or ObjectDisposedException) return false;
        }
        // Pool retirement can cancel a private lifetime token while the caller remains active.
        // The catch filter above excludes caller cancellation and client disposal before retrying it.
        return error is RespireConnectionException or RespireTimeoutException or IOException or SocketException or OperationCanceledException;
    }
}
