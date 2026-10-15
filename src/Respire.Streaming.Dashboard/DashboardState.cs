using System.Diagnostics;
using System.Text;
using Respire.Internal;

namespace Respire.Streaming.Dashboard;

internal sealed record DashboardSnapshot(IReadOnlyList<DashboardStreamSnapshot> Streams);
internal sealed record DashboardStreamSnapshot(int Index, RespireDashboardStream Configuration,
    long Length, long DeadLetterLength, RespireStreamGroupInfo[] Groups,
    RespireStreamPendingEntry[] Pending, RespireStreamEntry[] DeadLetters,
    double? DeliveriesPerSecond, string? Error);

internal sealed class DashboardState
{
    private readonly IRespireClient _client;
    private readonly RespireDashboardStream[] _streams;
    private readonly int _limit;
    private readonly (long Count, long Timestamp)?[] _samples;
    private readonly SemaphoreSlim _readGate = new(1, 1);

    internal DashboardState(IRespireClient client, RespireDashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (options.EntryLimit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Streams.Count is < 1 or > 100) throw new ArgumentException("Configure between 1 and 100 streams.", nameof(options));
        _client = client;
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
    }

    internal async Task<DashboardSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new DashboardStreamSnapshot[_streams.Length];
            for (var i = 0; i < _streams.Length; i++)
            {
                var stream = _streams[i];
                try
                {
                    var length = await _client.Streams.CountAsync(stream.Stream, cancellationToken).ConfigureAwait(false);
                    var groups = await _client.Streams.GroupInfoAsync(stream.Stream, cancellationToken).ConfigureAwait(false);
                    var pending = groups.Any(g => g.Name == stream.Group)
                        ? await _client.Streams.PendingAsync(stream.Stream, stream.Group,
                            count: _limit, cancellationToken: cancellationToken).ConfigureAwait(false) : [];
                    var deadLength = stream.DeadLetterStream is { } dead
                        ? await _client.Streams.CountAsync(dead, cancellationToken).ConfigureAwait(false) : 0;
                    var entries = stream.DeadLetterStream is { } deadStream
                        ? await _client.Streams.RangeAsync(deadStream, count: _limit,
                            cancellationToken: cancellationToken).ConfigureAwait(false) : [];
                    var count = groups.FirstOrDefault(g => g.Name == stream.Group).EntriesRead;
                    var now = Stopwatch.GetTimestamp();
                    double? rate = null;
                    if (count is { } current && _samples[i] is { } previous && current >= previous.Count && now > previous.Timestamp)
                        rate = (current - previous.Count) / Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                    _samples[i] = count is { } value ? (value, now) : null;
                    result[i] = new(i, stream, length, deadLength, groups, pending, entries, rate, null);
                }
                catch (RespireException)
                {
                    _samples[i] = null;
                    result[i] = new(i, stream, 0, 0, [], [], [], null, "Stream unavailable. Check the configured keys, group and Redis permissions.");
                }
            }
            return new(result);
        }
        finally { _readGate.Release(); }
    }

    internal async ValueTask<long> MutateAsync(int index, string id, bool retry, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= _streams.Length || _streams[index].DeadLetterStream is not { } dead) return -2;
        var stream = _streams[index];
        return await _client.Scripts.ExecuteIntegerAsync(DashboardScripts.Mutate,
            [stream.Stream, dead], [stream.Group, id, retry ? "retry" : "delete", stream.IsJobQueue ? "1" : "0"],
            cancellationToken).ConfigureAwait(false);
    }
}
