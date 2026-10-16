using System.Text;
using Respire.Internal;

namespace Respire.Streaming.Dashboard;

internal sealed record DashboardSnapshot(IReadOnlyList<DashboardStreamSnapshot> Streams);
internal sealed record DashboardJobLogs(string QueueName, string JobId, string[] Lines);
internal sealed record DashboardJobGroups(int Index, string QueueName, IReadOnlyList<RespireJobGroupInfo> Groups, string? NextCursor);
internal sealed record DashboardJobBatch(string Id, RespireJobBatchStatus? Status, bool Unavailable = false);
internal sealed record DashboardStreamSnapshot(int Index, RespireDashboardStream Configuration,
    long Length, long DeadLetterLength, RespireStreamGroupInfo[] Groups,
    RespireStreamPendingEntry[] Pending, RespireStreamEntry[] DeadLetters,
    double? DeliveriesPerSecond, string? Error, bool SourceUnavailable = false, bool DeadLettersUnavailable = false,
    bool? IsPaused = null, long? RateLimitHits = null, IReadOnlyList<RespireJobMetricBucket>? Metrics = null,
    RespireJobGroupPage? JobGroups = null, IReadOnlyList<DashboardJobBatch>? Batches = null);

internal sealed class DashboardState
{
    private readonly IRespireClient _client;
    private readonly RespireDashboardStream[] _streams;
    private readonly int _limit;
    private readonly int _metricsHistoryMinutes;
    private readonly RespireJobQueue<byte[]>?[] _batchQueues;
    private readonly (long Count, long Timestamp)?[] _samples;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly double?[] _rates;
    private DashboardSnapshot? _snapshot;
    private long _snapshotTimestamp;
    private long _version;
    private long _snapshotVersion;

    internal DashboardState(IRespireClient client, RespireDashboardOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (options.EntryLimit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MetricsHistoryMinutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Streams.Count is < 1 or > 100) throw new ArgumentException("Configure between 1 and 100 streams.", nameof(options));
        _client = client;
        _clock = clock ?? TimeProvider.System;
        _limit = options.EntryLimit;
        _metricsHistoryMinutes = options.MetricsHistoryMinutes;
        _streams = options.Streams.Select(s =>
        {
            var copy = new RespireDashboardStream
            {
                Name = s.Name, Stream = s.Stream, Group = s.Group,
                DeadLetterStream = s.DeadLetterStream, IsJobQueue = s.IsJobQueue,
            };
            foreach (var id in s.BatchIds) copy.BatchIds.Add(id);
            return copy;
        }).ToArray();
        _batchQueues = _streams.Select(stream => CreateBatchReader(client, stream, _limit)).ToArray();
        var deadKeys = new HashSet<string>(StringComparer.Ordinal);
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stream in _streams)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stream.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(stream.Stream);
            ArgumentException.ThrowIfNullOrWhiteSpace(stream.Group);
            var source = client.ResolveKey(stream.Stream).ToBytes();
            sourceKeys.Add(Convert.ToBase64String(source));
            if (stream.DeadLetterStream is not { } dead) continue;
            ArgumentException.ThrowIfNullOrWhiteSpace(dead);
            if (Encoding.UTF8.GetByteCount(stream.Group) > 256)
                throw new ArgumentException("Dead-letter group names must fit the worker's 256-byte metadata.", nameof(options));
            var target = client.ResolveKey(dead).ToBytes();
            if (source.AsSpan().SequenceEqual(target) || ClusterHash.GetSlot(source) != ClusterHash.GetSlot(target))
                throw new ArgumentException("Source and dead-letter keys must resolve to distinct keys in one Cluster slot.", nameof(options));
            if (!deadKeys.Add(Convert.ToBase64String(target)))
                throw new ArgumentException("Each dead-letter stream must belong to exactly one configured source/group.", nameof(options));
        }
        if (deadKeys.Overlaps(sourceKeys))
            throw new ArgumentException("A dead-letter stream cannot also be a configured source.", nameof(options));
        _samples = new (long, long)?[_streams.Length];
        _rates = new double?[_streams.Length];
    }

    private static RespireJobQueue<byte[]>? CreateBatchReader(IRespireClient client, RespireDashboardStream stream, int limit)
    {
        if (stream.BatchIds.Count == 0) return null;
        if (!stream.IsJobQueue || stream.BatchIds.Count > limit)
            throw new ArgumentException("Configure at most EntryLimit batch IDs on a job queue.");
        const string prefix = "{respire-job:";
        const string suffix = "}:ready";
        if (stream.Stream is null || !stream.Stream.StartsWith(prefix, StringComparison.Ordinal)
            || !stream.Stream.EndsWith(suffix, StringComparison.Ordinal) || stream.Stream.Length <= prefix.Length + suffix.Length)
            throw new ArgumentException("Batch inspection requires the job queue's logical StreamKey.");
        var utf8 = new UTF8Encoding(false, true);
        foreach (var id in stream.BatchIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (id.Length > 4096) throw new ArgumentException("Batch IDs must be at most 4096 characters.");
            _ = utf8.GetByteCount(id);
        }
        // The runtime owns ID hashing, key prefixing and decoding. Inspection never uses a payload codec.
        return new(client, stream.Stream[prefix.Length..^suffix.Length],
            static _ => throw new InvalidOperationException("Dashboard batch readers cannot enqueue jobs."));
    }

    internal async Task<DashboardSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = Interlocked.Read(ref _version);
            if (_snapshot is not null && _snapshotVersion == version
                && _clock.GetElapsedTime(_snapshotTimestamp) < TimeSpan.FromSeconds(2)) return _snapshot;
            var result = new DashboardStreamSnapshot[_streams.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, _streams.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (i, token) =>
            {
                var stream = _streams[i];
                long length = 0, deadLength = 0;
                RespireStreamGroupInfo[] groups = [];
                RespireStreamPendingEntry[] pending = [];
                RespireStreamEntry[] entries = [];
                string? error = null;
                var sourceUnavailable = false;
                var deadLettersUnavailable = false;
                bool? paused = null;
                long? rateLimitHits = null;
                IReadOnlyList<RespireJobMetricBucket>? metrics = null;
                RespireJobGroupPage? jobGroups = null;
                DashboardJobBatch[] batches = [];
                if (stream.IsJobQueue)
                {
                    if (_batchQueues[i] is { } queue)
                    {
                        batches = await ReadBatchesAsync(stream.BatchIds,
                            (id, ct) => queue.GetBatchStatusAsync(id, ct), token).ConfigureAwait(false);
                    }
                    try
                    {
                        jobGroups = await RespireJobGroups.ReadAsync(_client, stream.Stream, _limit,
                            cancellationToken: token).ConfigureAwait(false);
                    }
                    catch (RespireException)
                    {
                        error = "Application job groups unavailable. Check the group keys and Redis permissions.";
                    }
                    try
                    {
                        using var controls = await _client.Scripts.ExecuteAsync(QueueControlState,
                            [stream.Stream + ":control"], [], token).ConfigureAwait(false);
                        paused = controls[0].AsString() == "1";
                        rateLimitHits = long.Parse(controls[1].AsString(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch (RespireException)
                    {
                        error = (error is null ? "" : error + " ") + "Queue controls unavailable. Check the control key and Redis permissions.";
                    }
                    try
                    {
                        metrics = await RespireJobMetrics.ReadAsync(_client, stream.Stream, _metricsHistoryMinutes, token).ConfigureAwait(false);
                    }
                    catch (RespireException)
                    {
                        error = (error is null ? "" : error + " ") + "Metrics history unavailable. Check the metrics key and Redis permissions.";
                    }
                }
                try
                {
                    length = await _client.Streams.CountAsync(stream.Stream, token).ConfigureAwait(false);
                    groups = await _client.Streams.GroupInfoAsync(stream.Stream, token).ConfigureAwait(false);
                    pending = groups.Any(g => g.Name == stream.Group)
                        ? await _client.Streams.PendingAsync(stream.Stream, stream.Group,
                            count: _limit, cancellationToken: token).ConfigureAwait(false) : [];
                }
                catch (RespireException)
                {
                    sourceUnavailable = true;
                    error = (error is null ? "" : error + " ") + "Source stream unavailable. Check the configured key, group and Redis permissions.";
                }
                try
                {
                    if (stream.DeadLetterStream is { } dead)
                    {
                        deadLength = await _client.Streams.CountAsync(dead, token).ConfigureAwait(false);
                        entries = await _client.Streams.RangeAsync(dead, count: _limit, cancellationToken: token).ConfigureAwait(false);
                    }
                }
                catch (RespireException)
                {
                    deadLettersUnavailable = true;
                    error = (error is null ? "" : error + " ") + "Dead-letter stream unavailable. Check its key and Redis permissions.";
                }
                var count = sourceUnavailable ? null : groups.FirstOrDefault(g => g.Name == stream.Group).EntriesRead;
                var now = _clock.GetTimestamp();
                if (count is not { } current)
                {
                    _samples[i] = null;
                    _rates[i] = null;
                }
                else if (_samples[i] is not { } previous || current < previous.Count)
                {
                    _samples[i] = (current, now);
                    _rates[i] = null;
                }
                else if (_clock.GetElapsedTime(previous.Timestamp, now) >= TimeSpan.FromSeconds(5))
                {
                    _rates[i] = (current - previous.Count) / _clock.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                    _samples[i] = (current, now);
                }
                result[i] = new(i, stream, length, deadLength, groups, pending, entries, _rates[i], error,
                    sourceUnavailable, deadLettersUnavailable, paused, rateLimitHits, metrics, jobGroups, batches);
            }).ConfigureAwait(false);
            _snapshot = new(result);
            _snapshotTimestamp = _clock.GetTimestamp();
            _snapshotVersion = version;
            return _snapshot;
        }
        finally { _readGate.Release(); }
    }

    internal static async Task<DashboardJobBatch[]> ReadBatchesAsync(IList<string> ids,
        Func<string, CancellationToken, ValueTask<RespireJobBatchStatus?>> read, CancellationToken cancellationToken)
    {
        var batches = new DashboardJobBatch[ids.Count];
        // Four stream readers each issue at most four batch reads at once.
        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (index, token) =>
        {
            var id = ids[index];
            try
            {
                batches[index] = new(id, await read(id, token).ConfigureAwait(false));
            }
            catch (RespireException)
            {
                batches[index] = new(id, null, Unavailable: true);
            }
        }).ConfigureAwait(false);
        return batches;
    }

    private static readonly RespireScript QueueControlState = RespireScript.Create("""
        local values = redis.call('HMGET', KEYS[1], 'paused', 'hits')
        return {values[1] or '0', values[2] or '0'}
        """);

    internal async ValueTask<DashboardJobLogs?> ReadLogsAsync(int index, string jobId, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= _streams.Length || !_streams[index].IsJobQueue) return null;
        var stream = _streams[index];
        var lines = await _client.Lists.RangeAsync(JobLogKeys.ForJob(stream.Stream, jobId), -_limit, -1,
            cancellationToken).ConfigureAwait(false);
        return new(stream.Name, jobId, lines);
    }

    internal async ValueTask<DashboardJobGroups?> ReadGroupsAsync(int index, string? cursor, string? name,
        CancellationToken cancellationToken)
    {
        if (index < 0 || index >= _streams.Length || !_streams[index].IsJobQueue) return null;
        var stream = _streams[index];
        if (name is not null)
            return new(index, stream.Name,
                [await RespireJobGroups.GetAsync(_client, stream.Stream, name, cancellationToken).ConfigureAwait(false)], null);
        var page = await RespireJobGroups.ReadAsync(_client, stream.Stream, _limit, cursor, cancellationToken).ConfigureAwait(false);
        return new(index, stream.Name, page.Groups, page.NextCursor);
    }

    internal async ValueTask<bool> SetGroupPausedAsync(int index, string name, bool paused, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= _streams.Length || !_streams[index].IsJobQueue) return false;
        try
        {
            if (paused) await RespireJobGroups.PauseAsync(_client, _streams[index].Stream, name, cancellationToken).ConfigureAwait(false);
            else await RespireJobGroups.ResumeAsync(_client, _streams[index].Stream, name, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { Interlocked.Increment(ref _version); }
    }

    internal async ValueTask<long> MutateAsync(int index, string id, bool retry, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= _streams.Length || _streams[index].DeadLetterStream is not { } dead) return -2;
        var stream = _streams[index];
        try
        {
            return await _client.Scripts.ExecuteIntegerAsync(DashboardScripts.Mutate,
                [stream.Stream, dead], [stream.Group, id, retry ? "retry" : "delete", stream.IsJobQueue ? "1" : "0"],
                cancellationToken).ConfigureAwait(false);
        }
        finally { Interlocked.Increment(ref _version); }
    }
}
