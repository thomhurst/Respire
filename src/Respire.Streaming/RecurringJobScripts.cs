namespace Respire.Streaming;

internal static class RecurringJobScripts
{
    // KEYS: ready stream, definition hash, next-due sorted set, lexicographic
    // schedule index (all scores zero), shared cursor string. Preflight all
    // mutations: Redis scripts serialize operations but do not roll back errors.
    private const string Helpers = """
        local function checktype(key, expected)
            local kind = redis.call('TYPE', key).ok
            if kind ~= 'none' and kind ~= expected then error('WRONGTYPE recurring job key') end
        end
        local function permit(command, ...)
            if not redis.acl_check_cmd or not redis.acl_check_cmd(command, ...) then
                error('NOPERM recurring jobs require Redis 7.0 and queue command permissions')
            end
        end
        checktype(KEYS[1], 'stream')
        checktype(KEYS[2], 'hash')
        checktype(KEYS[3], 'zset')
        checktype(KEYS[4], 'zset')
        checktype(KEYS[5], 'string')

        """;

    internal static readonly RespireScript Upsert = RespireScript.Create(Helpers + """
        local old = redis.call('HGET', KEYS[2], ARGV[1])
        -- A transport retry of this revision must not rewind an already committed tick.
        if old == ARGV[2] then return 1 end
        local oldDue = redis.call('ZSCORE', KEYS[3], ARGV[1])
        permit('HSET', KEYS[2], ARGV[1], ARGV[2])
        permit('HDEL', KEYS[2], ARGV[1])
        permit('ZADD', KEYS[3], ARGV[3], ARGV[1])
        permit('ZREM', KEYS[3], ARGV[1])
        permit('ZADD', KEYS[4], 0, ARGV[1])
        redis.call('HSET', KEYS[2], ARGV[1], ARGV[2])
        local indexed = redis.pcall('ZADD', KEYS[3], ARGV[3], ARGV[1])
        if type(indexed) == 'table' and indexed.err then
            if old then redis.call('HSET', KEYS[2], ARGV[1], old)
            else redis.call('HDEL', KEYS[2], ARGV[1]) end
            return indexed
        end
        local ordered = redis.pcall('ZADD', KEYS[4], 0, ARGV[1])
        if type(ordered) == 'table' and ordered.err then
            if oldDue then redis.call('ZADD', KEYS[3], oldDue, ARGV[1])
            else redis.call('ZREM', KEYS[3], ARGV[1]) end
            if old then redis.call('HSET', KEYS[2], ARGV[1], old)
            else redis.call('HDEL', KEYS[2], ARGV[1]) end
            return ordered
        end
        return 1
        """);

    internal static readonly RespireScript Remove = RespireScript.Create(Helpers + """
        permit('HDEL', KEYS[2], ARGV[1])
        permit('ZREM', KEYS[3], ARGV[1])
        permit('ZREM', KEYS[4], ARGV[1])
        redis.call('ZREM', KEYS[3], ARGV[1])
        redis.call('ZREM', KEYS[4], ARGV[1])
        return redis.call('HDEL', KEYS[2], ARGV[1])
        """);

    internal static readonly RespireScript ReadDue = RespireScript.Create(Helpers + """
        -- Walk stable schedule IDs, not moving due-time scores. A catch-up
        -- backlog cannot repeatedly take the first slot; the cursor survives
        -- process restarts and is shared by competing scheduler instances.
        local cursor = redis.call('GET', KEYS[5])
        local ids = redis.call('ZRANGEBYLEX', KEYS[4], cursor and '(' .. cursor or '-', '+', 'LIMIT', 0, ARGV[2])
        if #ids == 0 and cursor then
            ids = redis.call('ZRANGEBYLEX', KEYS[4], '-', '+', 'LIMIT', 0, ARGV[2])
        end
        local result = {}
        for _, id in ipairs(ids) do
            local due = redis.call('ZSCORE', KEYS[3], id)
            if due and tonumber(due) <= tonumber(ARGV[1]) then
                local definition = redis.call('HGET', KEYS[2], id)
                if not definition then error('ERR recurring schedule definition is missing') end
                table.insert(result, {id, definition, due})
            else
                table.insert(result, {id})
            end
        end
        if #ids > 0 then
            permit('SET', KEYS[5], ids[#ids])
            redis.call('SET', KEYS[5], ids[#ids])
        end
        return result
        """);

    internal static readonly RespireScript CommitTick = RespireScript.Create(Helpers + """
        -- An upsert, remove, or another scheduler invalidates this snapshot.
        if redis.call('HGET', KEYS[2], ARGV[1]) ~= ARGV[2]
            or tonumber(redis.call('ZSCORE', KEYS[3], ARGV[1])) ~= tonumber(ARGV[3]) then return 0 end
        local fields = {'_respire.job.id', ARGV[5], '_respire.job.payload', ARGV[6],
            '_respire.job.schedule', ARGV[1], '_respire.job.tick', ARGV[7]}
        if ARGV[8] and ARGV[8] ~= '' then
            table.insert(fields, 'traceparent'); table.insert(fields, ARGV[8])
            if ARGV[9] and ARGV[9] ~= '' then
                table.insert(fields, 'tracestate'); table.insert(fields, ARGV[9])
            end
        end
        permit('XADD', KEYS[1], '*', unpack(fields))
        permit('XDEL', KEYS[1], '0-0')
        if ARGV[4] == '' then permit('ZREM', KEYS[3], ARGV[1])
        else permit('ZADD', KEYS[3], ARGV[4], ARGV[1]) end
        local id = redis.call('XADD', KEYS[1], '*', unpack(fields))
        local advanced
        if ARGV[4] == '' then advanced = redis.pcall('ZREM', KEYS[3], ARGV[1])
        else advanced = redis.pcall('ZADD', KEYS[3], ARGV[4], ARGV[1]) end
        if type(advanced) == 'table' and advanced.err then
            redis.call('XDEL', KEYS[1], id)
            return advanced
        end
        return 1
        """);
}
