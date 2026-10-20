using Respire.Internal;

namespace Respire.Streaming;

internal static class JobQueueScheduling
{
    internal static string[] Keys(string stream) => [stream, stream + ":scheduled", stream + ":due"];

    internal static void ValidateKeys(IRespireClient client, string[] keys)
    {
        var resolved = keys.Select(key => client.ResolveKey(key).ToBytes()).ToArray();
        for (var i = 1; i < resolved.Length; i++)
        {
            if (ClusterHash.GetSlot(resolved[0]) != ClusterHash.GetSlot(resolved[i])
                || resolved.Take(i).Any(key => key.AsSpan().SequenceEqual(resolved[i])))
                throw new ArgumentException("Queue keys must resolve to distinct keys in the same Cluster slot.");
        }
    }

    internal static void ValidateDuration(TimeSpan duration, string name, bool allowZero = false)
    {
        if (duration < TimeSpan.Zero || (!allowZero && duration == TimeSpan.Zero)
            || duration > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(name);
    }

    // Every script checks types and all cleanup permissions before its first write.
    // Redis scripts are atomic, but errors do not undo writes: append/index failures
    // roll back only the new staged entry, leaving the original pending job intact.
    internal const string Helpers = """
        local function now()
            local time = redis.call('TIME')
            return tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        end
        local function checktype(key, expected)
            local kind = redis.call('TYPE', key).ok
            if kind ~= 'none' and kind ~= expected then error('WRONGTYPE job queue key') end
        end
        local function permit(command, ...)
            if not redis.acl_check_cmd or not redis.acl_check_cmd(command, ...) then
                error('NOPERM job scheduling requires Redis 7.0 and queue command permissions')
            end
        end
        local function field(fields, name, fallback)
            for i = 1, #fields, 2 do if fields[i] == name then return fields[i+1] end end
            return fallback
        end

        """ + JobGroupScheduling.StageHelpers + """
        local function unstage(fields, due, id)
            local prefix = groupPrefix(fields)
            if prefix then
                removeGrouped(prefix, prefix .. string.format('%016.0f', due) .. '|' .. id)
            else redis.call('ZREM', KEYS[3], id) end
            redis.call('XDEL', KEYS[2], id)
        end
        local function stage(fields, due)
            local prefix = groupPrefix(fields)
            if prefix then groupPermissions() end
            local id = redis.call('XADD', KEYS[2], '*', unpack(fields))
            if prefix then
                local member = prefix .. string.format('%016.0f', due) .. '|' .. id
                local indexed = redis.pcall('ZADD', grouped, 0, member)
                if not (type(indexed) == 'table' and indexed.err) then
                    if not redis.call('ZSCORE', parked, prefix) then indexed = redis.pcall('ZADD', order, 0, prefix) end
                end
                if type(indexed) == 'table' and indexed.err then
                    unstage(fields, due, id)
                    return indexed
                end
                return id
            end
            local indexed = redis.pcall('ZADD', KEYS[3], due, id)
            if type(indexed) == 'table' and indexed.err then
                redis.call('XDEL', KEYS[2], id)
                return indexed
            end
            return id
        end
        checktype(KEYS[1], 'stream')
        checktype(KEYS[2], 'stream')
        checktype(KEYS[3], 'zset')

        """;

    private const string StagePermissions = """
        permit('XADD', KEYS[2], '*', 'field', 'value')
        permit('XDEL', KEYS[2], '0-0')
        permit('ZADD', KEYS[3], 0, '0-0')

        """;

    internal static readonly RespireScript Enqueue = RespireScript.Create("local groupOffset = 5\n" + Helpers
        + "local deduplicationKey = KEYS[4]\n" + JobDeduplication.Duplicate + StagePermissions + """
        permit('ZREM', KEYS[3], '0-0')
        permit('SET', KEYS[4], '0-0', 'PX', ARGV[3])
        local clock = now()
        local expires = tonumber(ARGV[6]) > 0 and clock + tonumber(ARGV[6]) or 0
        local fields = {'_respire.job.id', ARGV[1], '_respire.job.payload', ARGV[2],
            '_respire.job.priority', ARGV[5], '_respire.job.expires', tostring(expires),
            '_respire.job.attempts', '0'}
        if ARGV[12] == '1' then
            table.insert(fields, '_respire.job.deduplication'); table.insert(fields, 'Extend')
        end
        if ARGV[9] and ARGV[9] ~= '' then
            table.insert(fields, '_respire.job.group'); table.insert(fields, ARGV[9])
        end
        if ARGV[10] and tonumber(ARGV[10]) > 0 then
            table.insert(fields, '_respire.job.runtime'); table.insert(fields, ARGV[10])
            table.insert(fields, '_respire.job.checkpoint'); table.insert(fields, ARGV[11])
        end
        if ARGV[7] and ARGV[7] ~= '' then
            table.insert(fields, 'traceparent'); table.insert(fields, ARGV[7])
            if ARGV[8] and ARGV[8] ~= '' then table.insert(fields, 'tracestate'); table.insert(fields, ARGV[8]) end
        end
        local due = clock + tonumber(ARGV[4])
        local id = stage(fields, due)
        if type(id) == 'table' then return id end
        local recorded = redis.pcall('SET', KEYS[4], record(id, ARGV[12], ARGV[3]), 'PX', ARGV[3])
        if type(recorded) == 'table' and recorded.err then
            unstage(fields, due, id)
            return recorded
        end
        return {id, 1}
        """);

    private const string WorkerHelpers = "local groupOffset = 4\n" + Helpers;

    internal static readonly RespireScript Promote = RespireScript.Create(WorkerHelpers + """
        permit('XADD', KEYS[1], '*', 'field', 'value')
        permit('XDEL', KEYS[2], '0-0')
        permit('ZREM', KEYS[3], '0-0')
        local clock = now()
        local ids = redis.call('ZRANGEBYSCORE', KEYS[3], '-inf', clock, 'LIMIT', 0, ARGV[1])
        local entries = {}
        for position, id in ipairs(ids) do
            local rows = redis.call('XRANGE', KEYS[2], id, id)
            if #rows ~= 1 then
                redis.call('ZREM', KEYS[3], id)
            else
                table.insert(entries, {id = id, fields = rows[1][2], position = position,
                    priority = tonumber(field(rows[1][2], '_respire.job.priority', '0'))})
            end
        end
        table.sort(entries, function(a, b)
            if a.priority == b.priority then return a.position < b.position end
            return a.priority > b.priority
        end)
        local promoted = 0
        for _, entry in ipairs(entries) do
            local expires = tonumber(field(entry.fields, '_respire.job.expires', '0'))
            if expires == 0 or expires > clock then
                redis.call('XADD', KEYS[1], '*', unpack(entry.fields))
                promoted = promoted + 1
            end
            redis.call('ZREM', KEYS[3], entry.id)
            redis.call('XDEL', KEYS[2], entry.id)
        end
        return promoted
        """);

    internal static readonly RespireScript ReadGroups = RespireScript.Create(ReadGroupsSource);

    internal const string ReadGroupsSource = WorkerHelpers + """
        permit('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', ARGV[3], 'STREAMS', KEYS[1], '>')
        -- Existing immediate, flow and legacy scheduled jobs retain stream FIFO precedence.
        -- This also validates the consumer group before any grouped promotion mutation.
        local ready = redis.call('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', ARGV[3], 'STREAMS', KEYS[1], '>')
        if ready then return ready[1][2] end

        """ + JobGroupScheduling.Select + """
        if selected == 0 then return {} end
        local ready = redis.call('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', selected, 'STREAMS', KEYS[1], '>')
        return ready[1][2]
        """;

    private const string Fence = """
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[3], ARGV[3], 1)
        if #pending ~= 1 or pending[1][2] ~= ARGV[2] or tostring(pending[1][4]) ~= ARGV[4] then return 0 end

        """;

    private const string Acknowledge = StreamWorkerScripts.CapabilitySource + """
        local delete = ARGV[6] == '1' and supports(2)
        if delete then
            local command = redis.pcall('COMMAND', 'INFO', 'XACKDEL')
            if type(command) == 'table' and command.err then error(command.err) end
            delete = type(command) == 'table' and not command.err and command[1] and true or false
        end
        -- Preflight the selected acknowledgement before staging any retry body.
        if delete then
            if not redis.acl_check_cmd('XACKDEL', KEYS[1], ARGV[1], 'ACKED', 'IDS', 1, ARGV[3]) then
                error('NOPERM job acknowledgement is not permitted')
            end
        elseif not redis.acl_check_cmd('XACK', KEYS[1], ARGV[1], ARGV[3]) then
            error('NOPERM job acknowledgement is not permitted')
        end
        local function acknowledge()
            if delete then
                redis.call('XACKDEL', KEYS[1], ARGV[1], 'ACKED', 'IDS', 1, ARGV[3])
                return 1
            end
            return redis.call('XACK', KEYS[1], ARGV[1], ARGV[3])
        end

        """;

    internal static readonly RespireScript Retry = RespireScript.Create(WorkerHelpers + Fence + StagePermissions + Acknowledge + """
        local rows = redis.call('XRANGE', KEYS[1], ARGV[3], ARGV[3])
        if #rows ~= 1 then return acknowledge() end
        local fields = rows[1][2]
        local clock = now()
        local expires = tonumber(field(fields, '_respire.job.expires', '0'))
        if expires > 0 and expires <= clock then return acknowledge() end
        -- ARGV[4] is both the fenced delivery count and this copy's contribution;
        -- a newly staged copy starts its consumer-group delivery count from zero.
        local attempts = tonumber(field(fields, '_respire.job.attempts', '0')) + tonumber(ARGV[4])
        -- A cooperative yield does not spend this run's failure budget. Earlier
        -- abandoned deliveries of the same entry still count toward the limit.
        if ARGV[7] == 'yield' then attempts = attempts - 1 end
        local updated = false
        for i = 1, #fields, 2 do
            if fields[i] == '_respire.job.attempts' then fields[i+1] = tostring(attempts); updated = true end
        end
        if not updated then table.insert(fields, '_respire.job.attempts'); table.insert(fields, tostring(attempts)) end
        permit('ZREM', KEYS[3], '0-0')
        local due = clock + tonumber(ARGV[5])
        local id = stage(fields, due)
        if type(id) == 'table' then return id end
        local ok, result = pcall(acknowledge)
        if not ok then unstage(fields, due, id); return redis.error_reply(result) end
        return 1
        """);

    // Return 1 for a live delivery, -1 for expired, 0 for a superseded attempt.
    internal static readonly RespireScript CheckExpiry = RespireScript.Create(Fence + """
        local time = redis.call('TIME')
        local clock = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        if tonumber(ARGV[5]) > 0 and tonumber(ARGV[5]) <= clock then

        """ + Acknowledge + """
            acknowledge()
            return -1
        end
        return 1
        """);
}
