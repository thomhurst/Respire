namespace Respire.Streaming;

// All indexes are fixed, explicitly supplied keys. Group identities are encoded as
// lexicographic member prefixes, never as Redis keys (including in Cluster).
internal static class JobGroupScheduling
{
    internal static string[] Keys(string stream) =>
        [.. JobQueueScheduling.Keys(stream), stream + ":group-order", stream + ":group-jobs", stream + ":group-cursor",
            stream + ":group-policy", stream + ":group-leases", stream + ":group-owners", stream + ":group-parked"];

    internal const string StageHelpers = """
        local order, grouped, cursor = KEYS[groupOffset], KEYS[groupOffset+1], KEYS[groupOffset+2]
        local function groupPrefix(fields)
            local name = field(fields, '_respire.job.group', '')
            if name == '' then return nil end
            return (string.gsub(name, '.', function(c) return string.format('%02x', string.byte(c)) end)) .. '|'
        end

        """ + JobGroupControls.Helpers + """
        local function groupPermissions()
            controlPermissions()
            checktype(order, 'zset')
            checktype(grouped, 'zset')
            permit('ZRANGEBYLEX', grouped, '-', '+', 'LIMIT', 0, 1)
            permit('ZSCORE', order, 'group')
            permit('ZADD', order, 0, 'group')
            permit('ZADD', grouped, 0, 'job')
            permit('ZREM', order, 'group')
            permit('ZREM', grouped, 'job')
        end
        local function removeGrouped(prefix, member)
            redis.call('ZREM', grouped, member)
            if #redis.call('ZRANGEBYLEX', grouped, '[' .. prefix, '[' .. prefix .. string.char(255), 'LIMIT', 0, 1) == 0 then
                redis.call('ZREM', order, prefix)
            end
        end

        """;

    // Called only with no unread ready entries, inside the same script as XREADGROUP.
    // One group visit can dispatch one job; empty/delayed/expired entries consume a visit.
    internal const string Select = """
        checktype(order, 'zset')
        checktype(grouped, 'zset')
        checktype(cursor, 'string')
        permit('GET', cursor)
        permit('SET', cursor, 'group')
        permit('ZRANGEBYLEX', order, '-', '+', 'LIMIT', 0, 1)
        permit('ZRANGEBYLEX', grouped, '-', '+', 'LIMIT', 0, ARGV[4])
        permit('XRANGE', KEYS[2], '-', '+')
        permit('XADD', KEYS[1], '*', 'field', 'value')
        permit('XDEL', KEYS[1], '0-0')
        permit('XDEL', KEYS[2], '0-0')
        permit('ZREM', order, 'group')
        permit('ZREM', grouped, 'job')
        controlPermissions()
        local clock = now()
        local dueGroups = redis.call('ZRANGEBYSCORE', parked, '-inf', clock, 'LIMIT', 0, 256)
        for _, prefix in ipairs(dueGroups) do wake(prefix) end
        local last = redis.call('GET', cursor) or ''
        local selected = 0
        for visit = 1, tonumber(ARGV[3]) do
            local groups = redis.call('ZRANGEBYLEX', order, '(' .. last, '+', 'LIMIT', 0, 1)
            if #groups == 0 then groups = redis.call('ZRANGEBYLEX', order, '-', '+', 'LIMIT', 0, 1) end
            if #groups == 0 then break end
            local prefix = groups[1]
            local due = eligible(prefix, clock)
            if due > clock then
                park(prefix, due)
                last = prefix
            else
                local members = redis.call('ZRANGEBYLEX', grouped, '[' .. prefix,
                    '[' .. prefix .. string.format('%016.0f', clock) .. '|' .. string.char(255), 'LIMIT', 0, ARGV[4])
                local chosen, fields, id, priority
                for _, member in ipairs(members) do
                    local entryId = string.match(member, '|([^|]+)$')
                    local rows = redis.call('XRANGE', KEYS[2], entryId, entryId)
                    local candidate = #rows == 1 and rows[1][2] or nil
                    local rank = candidate and tonumber(field(candidate, '_respire.job.priority', '0')) or math.huge
                    local expires = candidate and tonumber(field(candidate, '_respire.job.expires', '0')) or 0
                    local waiting = candidate and blocked(candidate) and (expires == 0 or expires > clock)
                    if waiting then
                        -- Re-index a held follow-up beyond this bounded page without spending admission.
                        local next = prefix .. string.format('%016.0f', clock + 1000) .. '|' .. entryId
                        permit('ZADD', grouped, 0, next)
                        redis.call('ZADD', grouped, 0, next)
                        redis.call('ZREM', grouped, member)
                    elseif candidate and (not current(candidate) or (expires > 0 and expires <= clock)) then
                        retire(candidate)
                        candidate = nil; rank = math.huge
                    end
                    if not waiting and (not chosen or rank > priority) then
                        chosen, fields, id, priority = member, candidate, entryId, rank
                    end
                end
                local appended
                local previousTat = redis.call('HGET', policy, prefix .. 'tat')
                if fields then
                    appended = redis.call('XADD', KEYS[1], '*', unpack(fields))
                    local reserved = reserve(prefix, appended, ARGV[2] .. '|1', clock)
                    if type(reserved) == 'table' then redis.call('XDEL', KEYS[1], appended); return reserved end
                end
                local saved = redis.pcall('SET', cursor, prefix)
                if type(saved) == 'table' and saved.err then
                    if appended then
                        removeLease(appended); redis.call('XDEL', KEYS[1], appended)
                        if previousTat then redis.call('HSET', policy, prefix .. 'tat', previousTat)
                        else redis.call('HDEL', policy, prefix .. 'tat') end
                    end
                    return saved
                end
                if chosen then
                    removeGrouped(prefix, chosen)
                    redis.call('XDEL', KEYS[2], id)
                end
                if appended then selected = selected + 1 end
                last = prefix
            end
        end

        """;
}
