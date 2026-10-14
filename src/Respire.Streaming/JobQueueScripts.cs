using Respire.Internal;

namespace Respire.Streaming;

internal static class JobQueueScripts
{
    private const string Fence = """
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[3], ARGV[3], 1)
        if #pending ~= 1 or pending[1][2] ~= ARGV[2] or tostring(pending[1][4]) ~= ARGV[4] then return 0 end
        local entries = redis.call('XRANGE', KEYS[1], ARGV[3], ARGV[3])
        if #entries ~= 1 then return -1 end
        local job = ''
        for i = 1, #entries[1][2], 2 do
            if entries[1][2][i] == '_respire.job.id' then job = entries[1][2][i + 1]; break end
        end

        """;

    internal static readonly RespireScript Progress = RespireScript.Create(Fence + """
        redis.call('XADD', KEYS[2], '*', 'job_id', job, 'entry_id', ARGV[3],
            'attempt', ARGV[4], 'kind', 'progress', 'percent', ARGV[5])
        return 1
        """);

    // No automatic trimming: a failed completion must not irreversibly evict another event.
    // Preflight all writes and compensate runtime failures before acknowledging the delivery.
    private const string CompleteSource = Fence + """
        if KEYS[1] == KEYS[2] or (KEYS[3] and (KEYS[1] == KEYS[3] or KEYS[2] == KEYS[3])) then
            return redis.error_reply('ERR job streams must differ')
        end
        local fields = {'job_id', job, 'entry_id', ARGV[3], 'attempt', ARGV[4], 'kind', ARGV[5]}
        if KEYS[3] then table.insert(fields, 'reason'); table.insert(fields, ARGV[6]) end
        if not redis.acl_check_cmd('XADD', KEYS[2], '*', unpack(fields))
            or not redis.acl_check_cmd('XDEL', KEYS[2], '0-0')
            or not redis.acl_check_cmd('XACK', KEYS[1], ARGV[1], ARGV[3]) then
            return redis.error_reply('NOPERM job completion is not permitted')
        end
        local deadFields
        if KEYS[3] then
            if #entries[1][2] > 2048 then return redis.error_reply('ERR too many job fields') end
            deadFields = {'_respire.source_id', ARGV[3], '_respire.group', string.sub(ARGV[1], 1, 256),
                '_respire.attempt', ARGV[4], '_respire.reason', string.sub(ARGV[6], 1, 64),
                '_respire.exception_type', string.sub(ARGV[7], 1, 256)}
            for _, field in ipairs(entries[1][2]) do table.insert(deadFields, field) end
            if not redis.acl_check_cmd('XADD', KEYS[3], '*', unpack(deadFields))
                or not redis.acl_check_cmd('XDEL', KEYS[3], '0-0') then
                return redis.error_reply('NOPERM job dead-letter completion is not permitted')
            end
        end
        local event = redis.call('XADD', KEYS[2], '*', unpack(fields))
        local dead
        if deadFields then
            dead = redis.pcall('XADD', KEYS[3], '*', unpack(deadFields))
            if type(dead) == 'table' and dead.err then
                redis.call('XDEL', KEYS[2], event)
                return dead
            end
        end
        local function acknowledge()

        """;

    private const string CompleteSuffix = """

        end
        local acknowledged = acknowledge()
        if type(acknowledged) == 'table' and acknowledged.err then
            redis.call('XDEL', KEYS[2], event)
            if dead then redis.call('XDEL', KEYS[3], dead) end
            return acknowledged
        end
        return acknowledged
        """;

    internal static readonly RespireScript Complete = RespireScript.Create(CompleteSource
        + "return redis.pcall('XACK', KEYS[1], ARGV[1], ARGV[3])" + CompleteSuffix);

    // Run the existing capability-aware acknowledgement inside pcall so a denied XACKDEL
    // also rolls back the event. Redis executes the function without another client intervening.
    internal static readonly RespireScript CompleteAndDelete = RespireScript.Create(CompleteSource
        + "local function ack()\n" + StreamWorkerScripts.AckAndDeleteSource
        + "\nend\nlocal ok, result = pcall(ack)\nif not ok then return {err = result} end\nreturn result"
        + CompleteSuffix);
}
