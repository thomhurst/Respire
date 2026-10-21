namespace Respire.Streaming;

internal static class JobGroupControls
{
    // Fixed keys, even in Cluster. All lease members have score zero for bounded lexicographic queries.
    internal const string Helpers = """
        local policy, leases, owners, parked = KEYS[groupOffset+3], KEYS[groupOffset+4], KEYS[groupOffset+5], KEYS[groupOffset+6]
        local function controlPermissions()
            checktype(policy, 'hash'); checktype(leases, 'zset'); checktype(owners, 'hash'); checktype(parked, 'zset')
            checktype(order, 'zset'); checktype(grouped, 'zset')
            for _, key in ipairs({policy, owners}) do
                permit('HGET', key, 'field'); permit('HSET', key, 'field', 'value'); permit('HDEL', key, 'field')
            end
            for _, key in ipairs({leases, parked, order}) do
                permit('ZADD', key, 0, 'member'); permit('ZREM', key, 'member'); permit('ZSCORE', key, 'member')
            end
            permit('ZRANGEBYLEX', leases, '-', '+', 'LIMIT', 0, 256)
            permit('ZLEXCOUNT', leases, '-', '+')
            permit('ZRANGEBYSCORE', parked, '-inf', 0, 'LIMIT', 0, 256)
            permit('ZRANGEBYLEX', grouped, '-', '+', 'LIMIT', 0, 1)
        end
        local function wake(prefix)
            if #redis.call('ZRANGEBYLEX', grouped, '[' .. prefix, '[' .. prefix .. string.char(255), 'LIMIT', 0, 1) > 0 then
                redis.call('ZADD', order, 0, prefix)
            end
            redis.call('ZREM', parked, prefix)
        end
        local function park(prefix, untilTime)
            redis.call('ZADD', parked, untilTime, prefix)
            redis.call('ZREM', order, prefix)
        end
        local function liveStart(prefix, clock)
            return '(' .. prefix .. string.format('%016.0f', clock) .. '|' .. string.char(255)
        end
        local function eligible(prefix, clock)
            if redis.call('HGET', policy, prefix .. 'paused') == '1' then return 9007199254740991 end
            local due = clock
            local maximum = tonumber(redis.call('HGET', policy, prefix .. 'max') or '0')
            if maximum > 0 and redis.call('ZLEXCOUNT', leases, liveStart(prefix, clock), '[' .. prefix .. string.char(255)) >= maximum then
                local first = redis.call('ZRANGEBYLEX', leases, liveStart(prefix, clock), '[' .. prefix .. string.char(255), 'LIMIT', 0, 1)
                due = tonumber(string.sub(first[1], #prefix+1, #prefix+16))
            end
            local burst = tonumber(redis.call('HGET', policy, prefix .. 'burst') or '0')
            if burst > 0 then
                local interval = math.ceil(tonumber(redis.call('HGET', policy, prefix .. 'window')) / burst)
                local tat = tonumber(redis.call('HGET', policy, prefix .. 'tat') or '0')
                due = math.max(due, math.ceil(tat - (burst-1)*interval))
            end
            return due
        end
        local function removeLease(id)
            local member = redis.call('HGET', owners, id)
            if member then redis.call('ZREM', leases, member) end
            redis.call('HDEL', owners, id, id .. ':token')
        end
        local function reserve(prefix, id, token, clock)
            -- Bound cleanup independently of the group's backlog and concurrency setting.
            local expired = redis.call('ZRANGEBYLEX', leases, '[' .. prefix,
                '[' .. prefix .. string.format('%016.0f', clock) .. '|' .. string.char(255), 'LIMIT', 0, 256)
            for _, member in ipairs(expired) do
                local old = string.match(member, '|([^|]+)$')
                if redis.call('HGET', owners, old) == member then removeLease(old)
                else redis.call('ZREM', leases, member) end
            end
            local duration = tonumber(redis.call('HGET', policy, prefix .. 'lease') or '60000')
            local member = prefix .. string.format('%016.0f', clock + duration) .. '|' .. id
            local previous = redis.call('HGET', policy, prefix .. 'tat')
            local function commit()
                redis.call('HSET', owners, id, member, id .. ':token', token)
                redis.call('ZADD', leases, 0, member)
                local burst = tonumber(redis.call('HGET', policy, prefix .. 'burst') or '0')
                if burst > 0 then
                    local interval = math.ceil(tonumber(redis.call('HGET', policy, prefix .. 'window')) / burst)
                    redis.call('HSET', policy, prefix .. 'tat', string.format('%.6f', math.max(clock, tonumber(previous or '0')) + interval))
                end
            end
            local ok, error = pcall(commit)
            if not ok then
                removeLease(id)
                if previous then redis.call('HSET', policy, prefix .. 'tat', previous)
                else redis.call('HDEL', policy, prefix .. 'tat') end
                return {err = error}
            end
            return duration
        end

        """;

    private const string Standalone = "local groupOffset = 4\n" + JobQueueScheduling.Helpers;

    internal static readonly RespireScript Configure = RespireScript.Create(Standalone + """
        controlPermissions()
        -- Wake first: failure to write the new policy leaves the old policy authoritative.
        -- A subsequent selection rechecks it before admitting any delivery.
        wake(ARGV[1])
        redis.call('HSET', policy, ARGV[1] .. 'max', ARGV[2], ARGV[1] .. 'burst', ARGV[3],
            ARGV[1] .. 'window', ARGV[4], ARGV[1] .. 'lease', ARGV[5])
        return 1
        """);

    internal static readonly RespireScript Pause = RespireScript.Create(Standalone + """
        controlPermissions()
        local previous = redis.call('HGET', policy, ARGV[1] .. 'paused') or '0'
        local previousWake = redis.call('ZSCORE', parked, ARGV[1])
        if ARGV[2] == '1' then park(ARGV[1], 9007199254740991) else wake(ARGV[1]) end
        local saved = redis.pcall('HSET', policy, ARGV[1] .. 'paused', ARGV[2])
        if type(saved) == 'table' and saved.err then
            if previousWake then park(ARGV[1], previousWake) else wake(ARGV[1]) end
            return saved
        end
        return previous == ARGV[2] and 0 or 1
        """);

    internal static readonly RespireScript Release = RespireScript.Create(Standalone + """
        controlPermissions()
        if redis.call('HGET', owners, ARGV[2] .. ':token') ~= ARGV[1] .. '|' .. ARGV[3] then return 0 end
        local member = redis.call('HGET', owners, ARGV[2])
        local prefix = string.match(member, '^([^|]+|)')
        -- Waking is safe: selection rechecks pause, concurrency and GCRA before admission.
        wake(prefix)
        removeLease(ARGV[2])
        return 1
        """);

    internal static readonly RespireScript Renew = RespireScript.Create(Standalone + """
        controlPermissions()
        local id = ARGV[2]
        if redis.call('HGET', owners, id .. ':token') ~= ARGV[1] .. '|' .. ARGV[3] then return 0 end
        local member = redis.call('HGET', owners, id)
        local prefix = string.match(member, '^([^|]+|)')
        local clock = now()
        if tonumber(string.sub(member, #prefix+1, #prefix+16)) <= clock then return 0 end
        local pending = redis.call('XPENDING', KEYS[1], ARGV[4], id, id, 1)
        if #pending ~= 1 or pending[1][2] ~= ARGV[1] or tostring(pending[1][4]) ~= ARGV[3] then return 0 end
        local duration = tonumber(redis.call('HGET', policy, prefix .. 'lease') or '60000')
        local next = prefix .. string.format('%016.0f', clock + duration) .. '|' .. id
        redis.call('ZADD', leases, 0, next)
        local saved = redis.pcall('HSET', owners, id, next)
        if type(saved) == 'table' and saved.err then
            if next ~= member then redis.call('ZREM', leases, next) end
            return saved
        end
        if next ~= member then redis.call('ZREM', leases, member) end
        -- A shorter policy can move expiry ahead of the parked wake-up. Recompute
        -- admission so concurrency advances without bypassing pause or rate debt.
        if next < member and redis.call('ZSCORE', parked, prefix) then
            park(prefix, eligible(prefix, clock))
        end
        return duration
        """);

    // Completion scripts receive two extra keys. Their existing PEL fence still applies.
    internal static RespireScript Fence(RespireScript script) => RespireScript.Create("""
        local leaseOwners, leaseIndex = KEYS[#KEYS], KEYS[#KEYS-1]
        local leaseMember = redis.call('HGET', leaseOwners, ARGV[3])
        if not leaseMember or redis.call('HGET', leaseOwners, ARGV[3] .. ':token') ~= ARGV[2] .. '|' .. ARGV[4] then return 0 end
        local expiry = tonumber(string.match(leaseMember, '|(%d+)|'))
        local time = redis.call('TIME')
        if expiry <= tonumber(time[1])*1000 + math.floor(tonumber(time[2])/1000) then return 0 end
        if not redis.call('ZSCORE', leaseIndex, leaseMember) then return 0 end
        -- Hide extra keys from legacy completion code that uses optional key positions.
        KEYS[#KEYS] = nil; KEYS[#KEYS] = nil

        """ + script.Source);

    internal static readonly RespireScript Complete = Fence(JobQueueScripts.Complete);
    internal static readonly RespireScript CompleteAndDelete = Fence(JobQueueScripts.CompleteAndDelete);
    internal static readonly RespireScript CompleteWithMetrics = Fence(JobQueueScripts.CompleteWithMetrics);
    internal static readonly RespireScript CompleteAndDeleteWithMetrics = Fence(JobQueueScripts.CompleteAndDeleteWithMetrics);
    internal static readonly RespireScript Retry = Fence(JobQueueScheduling.Retry);
    internal static readonly RespireScript CheckExpiry = Fence(JobQueueScheduling.CheckExpiry);
    internal static readonly RespireScript Nack = Fence(Respire.Internal.StreamWorkerScripts.Nack);

    internal const string RecoverSource = Standalone + """
        controlPermissions()
        permit('XPENDING', KEYS[1], ARGV[1], '-', '+', 2)
        permit('XRANGE', KEYS[1], '-', '+')
        permit('XCLAIM', KEYS[1], ARGV[1], ARGV[2], 0, '0-0')
        permit('XACK', KEYS[1], ARGV[1], '0-0')
        local start = ARGV[4]
        local pending
        if replay then
            pending = redis.call('XPENDING', KEYS[1], ARGV[1], '(' .. start, '+', 2, ARGV[2])
        else pending = redis.call('XPENDING', KEYS[1], ARGV[1], start, '+', 2) end
        if #pending == 0 then return {'0-0', {}} end
        local item = pending[1]
        local cursor = replay and item[1] or (pending[2] and pending[2][1] or '0-0')
        -- XNACK releases ownership and reports idle=-1; it is immediately claimable.
        if not replay and item[3] >= 0 and item[3] < tonumber(ARGV[3]) then return {cursor, {}} end
        local rows = redis.call('XRANGE', KEYS[1], item[1], item[1])
        if #rows == 0 then
            redis.call('XACK', KEYS[1], ARGV[1], item[1]); removeLease(item[1]); return {cursor, {}}
        end
        if not current(rows[1][2]) then
            redis.call('XACK', KEYS[1], ARGV[1], item[1]); removeLease(item[1]); return {cursor, {}}
        end
        local prefix = groupPrefix(rows[1][2])
        local previousTat = prefix and redis.call('HGET', policy, prefix .. 'tat')
        local function undo()
            if not prefix then return end
            removeLease(item[1])
            if previousTat then redis.call('HSET', policy, prefix .. 'tat', previousTat)
            else redis.call('HDEL', policy, prefix .. 'tat') end
        end
        local clock = now()
        if prefix then
            local member = redis.call('HGET', owners, item[1])
            -- A parked own-consumer replay yields to normal reads immediately; the bounded
            -- recovery cursor will revisit it later. Do not walk its whole PEL at startup.
            if member and tonumber(string.match(member, '|(%d+)|')) > clock then return {replay and '0-0' or cursor, {}} end
            if eligible(prefix, clock) > clock then return {replay and '0-0' or cursor, {}} end
            removeLease(item[1])
            local reserved = reserve(prefix, item[1], ARGV[2] .. '|' .. tostring(item[4]+1), clock)
            if type(reserved) == 'table' then return reserved end
        end
        local claimed = redis.pcall('XCLAIM', KEYS[1], ARGV[1], ARGV[2], 0, item[1])
        if type(claimed) == 'table' and claimed.err then
            undo()
            return claimed
        end
        if #claimed == 0 then
            undo()
            return {cursor, {}}
        end
        return {cursor, {{item[1], claimed[1][2], tostring(item[4]+1)}}}
        """;
}
