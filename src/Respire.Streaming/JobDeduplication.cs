namespace Respire.Streaming;

/// <summary>How duplicate submissions affect the first job's deduplication window.</summary>
public enum RespireJobDeduplicationMode
{
    /// <summary>Keep the first payload and options without extending the original window.</summary>
    FirstSubmissionWins,
    /// <summary>Keep the first payload and options and renew its original window on every duplicate.</summary>
    Extend,
}

internal static class JobDeduplication
{
    // Default records retain their original stream-ID-only format. Extended records carry
    // the winning window, independent of the stream entry's promotion, retry or deletion.
    // Both enqueue scripts (including EXEC) execute this before any append.
    // Extend hits renew with one SET: a denied renewal leaves the record unchanged,
    // and a successful renewal returns without staging or appending anything to roll back.
    internal const string Duplicate = """
        local existing = redis.call('GET', deduplicationKey)
        if existing then
            local id, window = string.match(existing, '^(%d+%-%d+)|extend|(%d+)$')
            if id then
                redis.call('SET', deduplicationKey, existing, 'PX', window)
                return {id, 0}
            end
            return {existing, 0}
        end
        local function record(id, mode, window)
            if mode == '1' then return id .. '|extend|' .. window end
            return id
        end

        """;

    internal static void Validate(RespireJobDeduplicationMode mode)
    {
        if (mode is not (RespireJobDeduplicationMode.FirstSubmissionWins or RespireJobDeduplicationMode.Extend))
            throw new ArgumentOutOfRangeException(nameof(mode));
    }
}
