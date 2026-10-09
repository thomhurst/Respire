namespace Respire.Internal;

// Shared with the fake server so it recognizes these exact scripts, rather than
// pretending to interpret arbitrary Lua. Redis executes each script atomically.
internal static class StreamWorkerScripts
{
    internal const string ReplaySource = """
        local page = redis.call('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', ARGV[3], 'STREAMS', KEYS[1], ARGV[4])
        local entries = {}
        local cursor = '0-0'
        if page and page[1] then
            for _, entry in ipairs(page[1][2]) do
                cursor = entry[1]
                if entry[2] then
                    local pending = redis.call('XPENDING', KEYS[1], ARGV[1], entry[1], entry[1], 1)
                    if #pending == 1 then
                        table.insert(entries, {entry[1], entry[2], tostring(pending[1][4])})
                    end
                end
            end
        end
        return {cursor, entries}
        """;

    internal const string ClaimSource = """
        local page = redis.call('XAUTOCLAIM', KEYS[1], ARGV[1], ARGV[2], ARGV[3], ARGV[4], 'COUNT', ARGV[5])
        local entries = {}
        for _, entry in ipairs(page[2]) do
            local pending = redis.call('XPENDING', KEYS[1], ARGV[1], entry[1], entry[1], 1)
            if #pending == 1 then
                table.insert(entries, {entry[1], entry[2], tostring(pending[1][4])})
            end
        end
        return {page[1], entries}
        """;

    internal const string AckSource = """
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[3], ARGV[3], 1)
        if #pending == 1 and pending[1][2] == ARGV[2] and tostring(pending[1][4]) == ARGV[4] then
            return redis.call('XACK', KEYS[1], ARGV[1], ARGV[3])
        end
        return 0
        """;

    internal static readonly RespireScript Replay = RespireScript.Create(ReplaySource);
    internal static readonly RespireScript Claim = RespireScript.Create(ClaimSource);
    internal static readonly RespireScript Ack = RespireScript.Create(AckSource);
}
