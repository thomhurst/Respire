using Respire.Internal;

namespace Respire.Streaming;

internal static class ReliableQueueScripts
{
    private const string Helpers = """
        local function checktype(key, expected)
            local kind = redis.call('TYPE', key).ok
            if kind ~= 'none' and kind ~= expected then error('WRONGTYPE reliable queue key') end
        end
        local function permit(command, ...)
            if not redis.acl_check_cmd or not redis.acl_check_cmd(command, ...) then
                error('NOPERM reliable queue requires Redis 7+ and queue command permissions')
            end
        end
        local function now()
            local clock = redis.call('TIME')
            return tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
        end

        """;

    internal static readonly RespireScript Send = RespireScript.Create(Helpers + """
        checktype(KEYS[1], 'stream')
        if ARGV[2] == '1' then
            checktype(KEYS[2], 'string')
            local existing = redis.call('GET', KEYS[2])
            if existing then return existing end
            permit('SET', KEYS[2], '0-0', 'PX', ARGV[3])
        end
        permit('XADD', KEYS[1], '*', 'payload', ARGV[1])
        permit('XDEL', KEYS[1], '0-0')
        local id = redis.call('XADD', KEYS[1], '*', 'payload', ARGV[1])
        if ARGV[2] == '1' then
            local saved = redis.pcall('SET', KEYS[2], id, 'PX', ARGV[3])
            if type(saved) == 'table' and saved.err then
                redis.call('XDEL', KEYS[1], id)
                return saved
            end
        end
        return id
        """);

    private const string QueueState = Helpers + StreamWorkerScripts.CapabilitySource + """
        local group = 'respire-messages'
        checktype(KEYS[1], 'stream')
        checktype(KEYS[2], 'zset')
        checktype(KEYS[3], 'hash')
        checktype(KEYS[4], 'hash')
        local clock = now()
        -- Resolve capabilities on the serving server each time, including after failover.
        local nativeAck = supports(2)
        local function completionPermissions()
            permit('XACK', KEYS[1], group, '0-0')
            permit('XDEL', KEYS[1], '0-0')
            permit('ZREM', KEYS[2], '0-0')
            permit('HDEL', KEYS[3], '0-0')
            permit('HDEL', KEYS[4], '0-0')
            -- An optional native command denied by ACL uses the compatible path.
            if nativeAck then
                local ok, allowed = pcall(redis.acl_check_cmd, 'XACKDEL', KEYS[1], group, 'DELREF', 'IDS', 1, '0-0')
                nativeAck = ok and allowed
            end
        end
        local function remove(id)
            if nativeAck then
                local result = redis.pcall('XACKDEL', KEYS[1], group, 'DELREF', 'IDS', 1, id)
                if unavailable(result) then nativeAck = false
                elseif type(result) == 'table' and result.err then error(result.err) end
            end
            if not nativeAck then
                redis.call('XACK', KEYS[1], group, id)
                redis.call('XDEL', KEYS[1], id)
            end
            redis.call('ZREM', KEYS[2], id)
            redis.call('HDEL', KEYS[3], id)
            redis.call('HDEL', KEYS[4], id)
        end

        """;

    internal static readonly RespireScript Receive = RespireScript.Create(QueueState + """
        checktype(KEYS[5], 'stream')
        completionPermissions()
        permit('XGROUP', 'CREATE', KEYS[1], group, '0', 'MKSTREAM')
        permit('XREADGROUP', 'GROUP', group, 'queue', 'COUNT', ARGV[2], 'STREAMS', KEYS[1], '>')
        permit('XRANGE', KEYS[1], '-', '+')
        permit('ZRANGEBYSCORE', KEYS[2], '-inf', clock, 'LIMIT', 0, ARGV[2])
        permit('ZADD', KEYS[2], clock, '0-0')
        permit('HGET', KEYS[4], '0-0')
        permit('HSET', KEYS[4], '0-0', 1)
        permit('HSET', KEYS[3], '0-0', ARGV[4])
        permit('XADD', KEYS[5], '*', 'payload', '', 'source_id', '0-0', 'delivery_count', 1)
        permit('XDEL', KEYS[5], '0-0')
        local created = redis.pcall('XGROUP', 'CREATE', KEYS[1], group, '0', 'MKSTREAM')
        if type(created) == 'table' and created.err and not string.find(created.err, 'BUSYGROUP', 1, true) then
            return created
        end
        local result = {}
        local function deliver(entry, attempt)
            local payload
            for i = 1, #entry[2], 2 do
                if entry[2][i] == 'payload' then payload = entry[2][i+1]; break end
            end
            if not payload then error('ERR reliable queue entry has no payload') end
            if attempt >= tonumber(ARGV[3]) then
                local dead = redis.call('XADD', KEYS[5], '*', 'payload', payload,
                    'source_id', entry[1], 'delivery_count', attempt)
                -- Keep the original recoverable if completion unexpectedly fails.
                local ok, err = pcall(remove, entry[1])
                if not ok then redis.call('XDEL', KEYS[5], dead); error(err) end
            else
                redis.call('ZADD', KEYS[2], clock + tonumber(ARGV[1]), entry[1])
                redis.call('HSET', KEYS[3], entry[1], ARGV[4])
                redis.call('HSET', KEYS[4], entry[1], attempt + 1)
                table.insert(result, {entry[1], payload, attempt + 1})
            end
        end
        -- A deadline per entry supports different visibility durations and delayed NACKs.
        -- Global CLAIM idle thresholds and XNACK's immediate eligibility cannot express that contract.
        local due = redis.call('ZRANGEBYSCORE', KEYS[2], '-inf', clock, 'LIMIT', 0, ARGV[2])
        for _, id in ipairs(due) do
            local entries = redis.call('XRANGE', KEYS[1], id, id)
            if #entries == 0 then remove(id)
            else deliver(entries[1], tonumber(redis.call('HGET', KEYS[4], id) or '0')) end
        end
        local remaining = tonumber(ARGV[2]) - #due
        if remaining > 0 then
            local page = redis.call('XREADGROUP', 'GROUP', group, 'queue', 'COUNT', remaining, 'STREAMS', KEYS[1], '>')
            if page and page[1] then
                for _, entry in ipairs(page[1][2]) do deliver(entry, 0) end
            end
        end
        return result
        """);

    internal static readonly RespireScript Complete = RespireScript.Create(QueueState + """
        local id = ARGV[1]
        local receipt = redis.call('HGET', KEYS[3], id)
        local due = redis.call('ZSCORE', KEYS[2], id)
        if receipt ~= ARGV[2] or not due or tonumber(due) <= clock then return 0 end
        if tonumber(ARGV[3]) >= 0 then
            permit('ZADD', KEYS[2], clock, id)
            permit('HDEL', KEYS[3], id)
            redis.call('ZADD', KEYS[2], clock + tonumber(ARGV[3]), id)
            redis.call('HDEL', KEYS[3], id)
        else
            completionPermissions()
            remove(id)
        end
        return 1
        """);
}
