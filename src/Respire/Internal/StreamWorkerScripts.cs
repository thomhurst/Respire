namespace Respire.Internal;

// Shared with the fake server so it recognizes these exact scripts, rather than
// pretending to interpret arbitrary Lua. Redis executes each script atomically.
internal static class StreamWorkerScripts
{
    // Keep the Lua guard below in sync. Leave ample stack space for metadata and command arguments.
    internal const int MaximumDeadLetterFields = 1024;

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
            if entry and entry[2] then
                local pending = redis.call('XPENDING', KEYS[1], ARGV[1], entry[1], entry[1], 1)
                if #pending == 1 then
                    table.insert(entries, {entry[1], entry[2], tostring(pending[1][4])})
                end
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

    // Discovery executes inside the operation's script, on its actual serving server.
    // No capability evidence survives a redirect, failover, or reconnect. Denied INFO
    // and non-Redis implementations conservatively retain the compatible path.
    private const string CapabilitySource = """
        local function supports(minor)
            local info = redis.pcall('INFO', 'SERVER')
            if type(info) ~= 'string' or string.find(info, 'valkey_version:', 1, true) then return false end
            local major, version = string.match(info, 'redis_version:(%d+)%.(%d+)%.')
            return major and (tonumber(major) > 8 or (tonumber(major) == 8 and tonumber(version) >= minor))
        end
        local function unavailable(reply)
            return type(reply) == 'table' and reply.err
                and (string.find(reply.err, 'unknown command', 1, true) or reply.err == 'ERR syntax error')
        end

        """;

    internal const string CapabilityClaimSource = CapabilitySource + """
        if supports(4) then
            local page = redis.pcall('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', ARGV[5],
                'CLAIM', ARGV[3], 'STREAMS', KEYS[1], '>')
            if not unavailable(page) then
                if type(page) == 'table' and page.err then return page end
                local entries = {}
                if page and page[1] then
                    for _, entry in ipairs(page[1][2]) do
                        -- CLAIM reports the count BEFORE this delivery. New entries omit it.
                        if entry[2] then
                            table.insert(entries, {entry[1], entry[2], tostring(entry[4] and entry[4] + 1 or 1)})
                        else
                            -- Match XAUTOCLAIM cleanup when a pending body has been deleted.
                            redis.call('XACK', KEYS[1], ARGV[1], entry[1])
                        end
                    end
                end
                -- CLAIM may omit deleted entries entirely. Scan a bounded PEL page
                -- independently, retaining its cursor so live low IDs cannot hide them.
                local count = tonumber(ARGV[5])
                local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[4], '+', count + 1)
                for i = 1, math.min(#pending, count) do
                    local id = pending[i][1]
                    if #redis.call('XRANGE', KEYS[1], id, id) == 0 then
                        redis.call('XACK', KEYS[1], ARGV[1], id)
                    end
                end
                return {pending[count + 1] and pending[count + 1][1] or '0-0', entries}
            end
        end

        """ + ClaimSource;

    internal const string AckAndDeleteSource = CapabilitySource + """
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[3], ARGV[3], 1)
        if #pending ~= 1 or pending[1][2] ~= ARGV[2] or tostring(pending[1][4]) ~= ARGV[4] then return 0 end
        if supports(2) then
            local reply = redis.pcall('XACKDEL', KEYS[1], ARGV[1], 'ACKED', 'IDS', 1, ARGV[3])
            if not unavailable(reply) then
                if type(reply) == 'table' and reply.err then return reply end
                return 1
            end
        end
        return redis.call('XACK', KEYS[1], ARGV[1], ARGV[3])
        """;

    internal const string NackSource = CapabilitySource + """
        if not supports(8) then return 0 end
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[3], ARGV[3], 1)
        if #pending ~= 1 or pending[1][2] ~= ARGV[2] or tostring(pending[1][4]) ~= ARGV[4] then return 0 end
        -- XNACK bypasses every CLAIM idle threshold. Leave young failures pending
        -- for ordinary recovery without holding a reader until they become eligible.
        if pending[1][3] < tonumber(ARGV[5]) then return 0 end
        local reply = redis.pcall('XNACK', KEYS[1], ARGV[1], 'FAIL', 'IDS', 1, ARGV[3])
        if unavailable(reply) then return 0 end
        if type(reply) == 'table' and reply.err then return reply end
        return 0
        """;

    internal static readonly RespireScript CapabilityClaim = RespireScript.Create(CapabilityClaimSource);
    internal static readonly RespireScript AckAndDelete = RespireScript.Create(AckAndDeleteSource);
    internal static readonly RespireScript Nack = RespireScript.Create(NackSource);

    internal const string DeadLetterSource = """
        if KEYS[1] == KEYS[2] then return redis.error_reply('ERR source and dead-letter keys must differ') end
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[3], ARGV[3], 1)
        if #pending ~= 1 or pending[1][2] ~= ARGV[2] or tostring(pending[1][4]) ~= ARGV[4] then return 0 end
        local entries = redis.call('XRANGE', KEYS[1], ARGV[3], ARGV[3])
        -- The body can be deleted while its fenced delivery is still pending.
        if #entries ~= 1 then return redis.call('XACK', KEYS[1], ARGV[1], ARGV[3]) end
        if #entries[1][2] > 2048 then
            return redis.error_reply('ERR dead-letter entries support at most 1024 field/value pairs')
        end
        local targetType = redis.call('TYPE', KEYS[2]).ok
        if targetType ~= 'none' and targetType ~= 'stream' then
            return redis.error_reply('WRONGTYPE dead-letter key is not a stream')
        end
        local fields = {'_respire.source_id', ARGV[3], '_respire.group', string.sub(ARGV[1], 1, 256),
            '_respire.attempt', ARGV[4], '_respire.reason', string.sub(ARGV[5], 1, 64),
            '_respire.exception_type', string.sub(ARGV[6], 1, 256)}
        for _, field in ipairs(entries[1][2]) do table.insert(fields, field) end
        -- Scripts do not roll back writes: preflight both mutations before XADD.
        if not redis.acl_check_cmd('XADD', KEYS[2], '*', unpack(fields))
            or not redis.acl_check_cmd('XACK', KEYS[1], ARGV[1], ARGV[3]) then
            return redis.error_reply('NOPERM dead-letter completion is not permitted')
        end
        redis.call('XADD', KEYS[2], '*', unpack(fields))
        return redis.call('XACK', KEYS[1], ARGV[1], ARGV[3])
        """;

    internal static readonly RespireScript DeadLetter = RespireScript.Create(DeadLetterSource);
}
