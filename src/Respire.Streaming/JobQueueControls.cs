using Respire.Internal;

namespace Respire.Streaming;

internal static class JobQueueControls
{
    internal static string Key(string stream) => stream + ":control";

    internal static readonly RespireScript Pause = RespireScript.Create("""
        local previous = redis.call('HGET', KEYS[1], 'paused')
        if (previous or '0') == ARGV[1] then return 0 end
        local kind = ARGV[1] == '1' and 'paused' or 'resumed'
        if not redis.acl_check_cmd('HSET', KEYS[1], 'paused', ARGV[1])
            or not redis.acl_check_cmd('HDEL', KEYS[1], 'paused')
            or not redis.acl_check_cmd('XADD', KEYS[2], '*', 'kind', kind) then
            return redis.error_reply('NOPERM queue pause transition is not permitted')
        end
        redis.call('HSET', KEYS[1], 'paused', ARGV[1])
        local event = redis.pcall('XADD', KEYS[2], '*', 'kind', kind,
            'job_id', '', 'entry_id', '0-0', 'attempt', '0')
        if type(event) == 'table' and event.err then
            if previous then redis.call('HSET', KEYS[1], 'paused', previous)
            else redis.call('HDEL', KEYS[1], 'paused') end
            return event
        end
        return 1
        """);

    internal static readonly RespireScript Configure = RespireScript.Create("""
        local limit = redis.call('HGET', KEYS[1], 'limit')
        if ARGV[3] == 'initialize' and limit then
            if limit ~= ARGV[1] or redis.call('HGET', KEYS[1], 'duration') ~= ARGV[2] then
                return redis.error_reply('ERR queue rate limit differs; use SetRateLimitAsync to change the shared policy')
            end
            return 0
        end
        -- Keep the active window and its usage when changing policy.
        redis.call('HSET', KEYS[1], 'limit', ARGV[1], 'duration', ARGV[2])
        return 1
        """);

    internal static readonly RespireScript State = RespireScript.Create("""
        local values = redis.call('HMGET', KEYS[1], 'paused', 'hits')
        return {values[1] or '0', values[2] or '0'}
        """);

    private const string ReadSource = """
        local page = redis.call('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', 1, 'STREAMS', KEYS[1], '>')
        local entries = {}
        if page and page[1] then
            for _, entry in ipairs(page[1][2]) do
                table.insert(entries, {entry[1], entry[2], '1'})
            end
        end
        return {'0-0', entries}
        """;

    // One admission per reader avoids reserving a batch that could start long after a pause.
    // Reuse the stream worker's replay/recovery fencing and capability handling unchanged.
    internal static readonly RespireScript Read = Wrap(ReadSource, "'0-0'");
    internal static readonly RespireScript Replay = Wrap(StreamWorkerScripts.ReplaySource, "ARGV[4]");
    internal static readonly RespireScript Claim = Wrap(StreamWorkerScripts.CapabilityClaimSource, "ARGV[4]");

    private static RespireScript Wrap(string source, string cursor) => RespireScript.Create("local cursor = " + cursor + "\n" + """
        if redis.call('HGET', KEYS[2], 'paused') == '1' then return {cursor, {}, 1, 0} end
        local limit = tonumber(redis.call('HGET', KEYS[2], 'limit') or '0')
        local count = 0
        if limit > 0 then
            local time = redis.call('TIME')
            local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
            local untilTime = tonumber(redis.call('HGET', KEYS[2], 'until') or '0')
            count = tonumber(redis.call('HGET', KEYS[2], 'count') or '0')
            if untilTime <= now then
                untilTime = now + tonumber(redis.call('HGET', KEYS[2], 'duration'))
                count = 0
            end
            if count >= limit then
                redis.call('HINCRBY', KEYS[2], 'hits', 1)
                return {cursor, {}, 0, untilTime - now}
            end
            -- Reserve before claiming. An ambiguous failure can waste a permit, never exceed the limit.
            redis.call('HSET', KEYS[2], 'until', untilTime, 'count', count + 1)
        end
        local function read()

        """ + source + "\nend\n" + """
        local page = read()
        if page.err then return page end
        if limit > 0 and #page[2] == 0 then redis.call('HSET', KEYS[2], 'count', count) end
        return {page[1], page[2], 0, 0}
        """);
}
