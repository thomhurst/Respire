namespace Respire.Streaming;

/// <summary>Terminal deliveries in a UTC minute. Failed counts dead letters, not retried handler attempts.</summary>
public sealed record RespireJobMetricBucket(DateTimeOffset Minute, long Completed, long Failed);

/// <summary>Bounded historical counters for job queues, using ordinary Redis hashes.</summary>
public static class RespireJobMetrics
{
    /// <summary>Reads at most 1440 minute buckets, oldest first, including the current partial minute.</summary>
    /// <remarks>Uses the Redis clock and the logical source stream key. Missing buckets are zero within the
    /// retained window; an empty result means no retained metrics. Disabled workers do not update existing history.</remarks>
    public static async ValueTask<IReadOnlyList<RespireJobMetricBucket>> ReadAsync(IRespireClient client,
        string streamKey, int minutes = 60, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        using var reply = await client.Scripts.ExecuteAsync(JobMetricsScripts.Read, [streamKey + ":metrics"],
            [minutes], cancellationToken).ConfigureAwait(false);
        var result = new RespireJobMetricBucket[reply.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new(DateTimeOffset.FromUnixTimeSeconds(reply[i][0].AsInteger() * 60),
                reply[i][1].AsInteger(), reply[i][2].AsInteger());
        return result;
    }
}

internal static class JobMetricsScripts
{
    // Remove the final declared key so the existing optional dead-letter and flow key positions stay intact.
    internal const string Key = "local metricsKey = table.remove(KEYS)\n";

    internal const string Prepare = """
        local saveMetrics, rollbackMetrics
        -- Cancelled members do not contribute metrics and must not depend on metrics storage or ACLs.
        if kind ~= 'cancelled' then
            local retention = tonumber(ARGV[10])
            if not retention or retention < 1 or retention > 1440 or retention ~= math.floor(retention) then
                return redis.error_reply('ERR invalid job metrics retention')
            end
            local storedRetention = redis.call('HGET', metricsKey, 'retention')
            if storedRetention and tonumber(storedRetention) ~= retention then
                return redis.error_reply('ERR job metrics retention must match all workers; stop workers and clear metrics to change it')
            end
            local clock = redis.call('TIME')
            local now = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
            local minute = math.floor(now / 60000)
            local slot = tostring(minute % retention)
            local oldBucket = redis.call('HGET', metricsKey, slot)
            local oldTtl = redis.call('PTTL', metricsKey)
            local bucket = oldBucket and cmsgpack.unpack(oldBucket) or {minute, 0, 0}
            if bucket[1] ~= minute then bucket = {minute, 0, 0} end
            local counter = ARGV[5] == 'completed' and 2 or 3
            bucket[counter] = bucket[counter] + 1
            local newBucket = cmsgpack.pack(bucket)
            if not redis.acl_check_cmd('HSET', metricsKey, slot, newBucket, 'retention', retention)
                or not redis.acl_check_cmd('HDEL', metricsKey, slot)
                or not redis.acl_check_cmd('DEL', metricsKey)
                or not redis.acl_check_cmd('PERSIST', metricsKey)
                or not redis.acl_check_cmd('PEXPIREAT', metricsKey, (minute + retention) * 60000) then
                return redis.error_reply('NOPERM job metrics update is not permitted')
            end
            local metricsSaved = false
            rollbackMetrics = function()
                if not metricsSaved then return end
                if not storedRetention then redis.call('DEL', metricsKey); return end
                if oldBucket then redis.call('HSET', metricsKey, slot, oldBucket)
                else redis.call('HDEL', metricsKey, slot) end
                if oldTtl >= 0 then redis.call('PEXPIREAT', metricsKey, now + oldTtl)
                else redis.call('PERSIST', metricsKey) end
            end
            saveMetrics = function()
                redis.call('HSET', metricsKey, slot, newBucket, 'retention', retention)
                metricsSaved = true
                redis.call('PEXPIREAT', metricsKey, (minute + retention) * 60000)
            end
        end

        """;

    internal const string Commit = """
        local metricsOk, metricsError = pcall(saveMetrics)
        if not metricsOk then
            rollbackMetrics()
            rollbackFlow()
            rollbackBatch()
            redis.call('XDEL', KEYS[2], event)
            if dead then redis.call('XDEL', KEYS[3], dead) end
            return redis.error_reply(metricsError)
        end

        """;

    internal static readonly RespireScript Read = RespireScript.Create("""
        local retention = tonumber(redis.call('HGET', KEYS[1], 'retention'))
        if not retention then return {} end
        if retention < 1 or retention > 1440 or retention ~= math.floor(retention) then
            return redis.error_reply('ERR invalid job metrics retention')
        end
        local minute = math.floor(tonumber(redis.call('TIME')[1]) / 60)
        local count = math.min(retention, tonumber(ARGV[1]))
        local result = {}
        for at = minute - count + 1, minute do
            local value = redis.call('HGET', KEYS[1], tostring(at % retention))
            local bucket = value and cmsgpack.unpack(value) or {at, 0, 0}
            if bucket[1] ~= at then bucket = {at, 0, 0} end
            table.insert(result, bucket)
        end
        return result
        """);
}
