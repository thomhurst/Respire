namespace Respire.Streaming;

internal static class ReliableTopicScripts
{
    private const string Helpers = """
        local function checktype(key, expected)
            local kind = redis.call('TYPE', key).ok
            if kind ~= 'none' and kind ~= expected then error('WRONGTYPE reliable topic key') end
        end
        local function permit(command, ...)
            if not redis.acl_check_cmd or not redis.acl_check_cmd(command, ...) then
                error('NOPERM reliable topic requires Redis 7+ and topic command permissions')
            end
        end
        local function now()
            local clock = redis.call('TIME')
            return tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
        end

        """;

    internal static readonly RespireScript Publish = RespireScript.Create(Helpers + """
        checktype(KEYS[1], 'stream')
        local args = {}
        if tonumber(ARGV[2]) > 0 then args = {'MAXLEN', '=', ARGV[2]}
        elseif tonumber(ARGV[3]) > 0 then
            args = {'MINID', '=', string.format('%.0f', math.max(0, now() - tonumber(ARGV[3]))) .. '-0'}
        end
        table.insert(args, '*'); table.insert(args, 'payload'); table.insert(args, ARGV[1])
        permit('XADD', KEYS[1], unpack(args))
        return redis.call('XADD', KEYS[1], unpack(args))
        """);

    private const string State = Helpers + """
        local group = ARGV[1]
        checktype(KEYS[1], 'stream')
        checktype(KEYS[2], 'zset')
        checktype(KEYS[3], 'hash')
        checktype(KEYS[4], 'hash')
        local clock = now()
        local function completionPermissions()
            permit('XACK', KEYS[1], group, '0-0')
            permit('ZREM', KEYS[2], '0-0')
            permit('HDEL', KEYS[3], '0-0')
            permit('HDEL', KEYS[4], '0-0')
        end
        local function remove(id)
            -- Never delete the source: other subscriptions and replay still need it.
            redis.call('XACK', KEYS[1], group, id)
            redis.call('ZREM', KEYS[2], id)
            redis.call('HDEL', KEYS[3], id)
            redis.call('HDEL', KEYS[4], id)
        end

        """;

    internal static readonly RespireScript Receive = RespireScript.Create(State + """
        checktype(KEYS[5], 'stream')
        completionPermissions()
        permit('XREADGROUP', 'GROUP', group, 'topic', 'COUNT', ARGV[3], 'STREAMS', KEYS[1], '>')
        permit('XRANGE', KEYS[1], '-', '+')
        permit('ZRANGEBYSCORE', KEYS[2], '-inf', clock, 'LIMIT', 0, ARGV[3])
        permit('ZADD', KEYS[2], clock, '0-0')
        permit('HGET', KEYS[4], '0-0')
        permit('HSET', KEYS[4], '0-0', 1)
        permit('HSET', KEYS[3], '0-0', ARGV[5])
        permit('XADD', KEYS[5], '*', 'payload', '', 'source_id', '0-0', 'delivery_count', 1, 'subscription', group)
        permit('XDEL', KEYS[5], '0-0')
        local result = {}
        local function deliver(entry, attempt)
            local payload
            for i = 1, #entry[2], 2 do
                if entry[2][i] == 'payload' then payload = entry[2][i+1]; break end
            end
            if not payload then error('ERR reliable topic entry has no payload') end
            if attempt >= tonumber(ARGV[4]) then
                local dead = redis.call('XADD', KEYS[5], '*', 'payload', payload,
                    'source_id', entry[1], 'delivery_count', attempt, 'subscription', group)
                local ok, err = pcall(remove, entry[1])
                if not ok then redis.call('XDEL', KEYS[5], dead); error(err) end
            else
                redis.call('ZADD', KEYS[2], clock + tonumber(ARGV[2]), entry[1])
                redis.call('HSET', KEYS[3], entry[1], ARGV[5])
                redis.call('HSET', KEYS[4], entry[1], attempt + 1)
                table.insert(result, {entry[1], payload, attempt + 1})
            end
        end
        local due = redis.call('ZRANGEBYSCORE', KEYS[2], '-inf', clock, 'LIMIT', 0, ARGV[3])
        for _, id in ipairs(due) do
            local entries = redis.call('XRANGE', KEYS[1], id, id)
            if #entries == 0 then remove(id)
            else deliver(entries[1], tonumber(redis.call('HGET', KEYS[4], id) or '0')) end
        end
        local remaining = tonumber(ARGV[3]) - #due
        if remaining > 0 then
            local page = redis.call('XREADGROUP', 'GROUP', group, 'topic', 'COUNT', remaining, 'STREAMS', KEYS[1], '>')
            if page and page[1] then
                for _, entry in ipairs(page[1][2]) do deliver(entry, 0) end
            end
        end
        return result
        """);

    internal static readonly RespireScript Complete = RespireScript.Create(State + """
        local id = ARGV[2]
        local receipt = redis.call('HGET', KEYS[3], id)
        local due = redis.call('ZSCORE', KEYS[2], id)
        if receipt ~= ARGV[3] or not due or tonumber(due) <= clock then return 0 end
        if tonumber(ARGV[4]) >= 0 then
            permit('ZADD', KEYS[2], clock, id)
            permit('HDEL', KEYS[3], id)
            redis.call('ZADD', KEYS[2], clock + tonumber(ARGV[4]), id)
            redis.call('HDEL', KEYS[3], id)
        else
            completionPermissions()
            remove(id)
        end
        return 1
        """);

    internal static readonly RespireScript Seek = RespireScript.Create(Helpers + """
        permit('XPENDING', KEYS[1], ARGV[1])
        permit('XGROUP', 'SETID', KEYS[1], ARGV[1], ARGV[2])
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1])
        if pending[1] ~= 0 then return redis.error_reply('ERR cannot seek a subscription with pending deliveries') end
        return redis.call('XGROUP', 'SETID', KEYS[1], ARGV[1], ARGV[2])
        """);
}
