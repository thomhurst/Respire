using System.Text;
using Respire.Internal;

namespace Respire.Streaming.Dashboard;

internal sealed record DashboardSnapshot(IReadOnlyList<DashboardStreamSnapshot> Streams);
internal sealed record DashboardStreamSnapshot(int Index, RespireDashboardStream Configuration,
    long Length, long DeadLetterLength, RespireStreamGroupInfo[] Groups,
    RespireStreamPendingEntry[] Pending, RespireStreamEntry[] DeadLetters,
    double? DeliveriesPerSecond, string? Error, bool SourceUnavailable = false, bool DeadLettersUnavailable = false,
    bool? IsPaused = null, long? RateLimitHits = null);

internal sealed class DashboardState
{
    private readonly IRespireClient _client;
    private readonly RespireDashboardStream[] _streams;
    private readonly int _limit;
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
        if (options.Streams.Count is < 1 or > 100) throw new ArgumentException("Configure between 1 and 100 streams.", nameof(options));
        _client = client;
        _clock = clock ?? TimeProvider.System;
        _limit = options.EntryLimit;
        _streams = options.Streams.Select(s => new RespireDashboardStream
        {
            Name = s.Name, Stream = s.Stream, Group = s.Group,
            DeadLetterStream = s.DeadLetterStream, IsJobQueue = s.IsJobQueue,
        }).ToArray();
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
                if (stream.IsJobQueue)
                {
                    try
                    {
                        using var controls = await _client.Scripts.ExecuteAsync(QueueControlState,
                            [stream.Stream + ":control"], [], token).ConfigureAwait(false);
                        paused = controls[0].AsString() == "1";
                        rateLimitHits = long.Parse(controls[1].AsString(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch (RespireException)
                    {
                        error = "Queue controls unavailable. Check the control key and Redis permissions.";
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
                    sourceUnavailable, deadLettersUnavailable, paused, rateLimitHits);
            }).ConfigureAwait(false);
            _snapshot = new(result);
            _snapshotTimestamp = _clock.GetTimestamp();
            _snapshotVersion = version;
            return _snapshot;
        }
        finally { _readGate.Release(); }
    }

    private static readonly RespireScript QueueControlState = RespireScript.Create("""
        local values = redis.call('HMGET', KEYS[1], 'paused', 'hits')
        return {values[1] or '0', values[2] or '0'}
        """);

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
