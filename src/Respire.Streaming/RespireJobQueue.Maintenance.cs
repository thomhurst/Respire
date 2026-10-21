namespace Respire.Streaming;

public sealed partial class RespireJobQueue<T>
{
    /// <summary>Removes pending deduplication lifecycle fields whose current envelope is absent from both job streams.</summary>
    /// <param name="maximumEntries">Positive limit on lifecycle fields plus ready and scheduled entries examined. Defaults to 10000.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation; cannot undo a script already executed by Redis.</param>
    /// <returns>The number of orphaned pending fields removed, including held follow-ups.</returns>
    /// <remarks>Runs one atomic maintenance script, blocking Redis while it scans. Exceeding the limit fails without
    /// deleting fields. Active generations are always retained, including retries and handlers whose stream was
    /// manually deleted. Deduplication windows and scheduling indexes are unchanged. Requires HLEN/HGETALL/HDEL
    /// on the lifecycle hash and XRANGE on both streams. No producer or worker downtime is required.</remarks>
    public ValueTask<long> ReclaimOrphanedDeduplicationAsync(int maximumEntries = 10000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        string[] keys = [StreamKey, StreamKey + ":scheduled", JobDeduplication.Key(StreamKey)];
        JobQueueScheduling.ValidateKeys(_client, keys);
        return _client.Scripts.ExecuteIntegerAsync(JobDeduplication.ReclaimOrphans, [.. keys],
            [maximumEntries], cancellationToken);
    }
}
