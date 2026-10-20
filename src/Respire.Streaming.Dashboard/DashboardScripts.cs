namespace Respire.Streaming.Dashboard;

internal static class DashboardScripts
{
    // Only project identity and policy: payloads never leave Redis for this inspection.
    internal static readonly RespireScript Deduplication = RespireScript.Create("""
        local result = {}
        for _, id in ipairs(ARGV) do
            local rows = redis.call('XRANGE', KEYS[1], id, id)
            if #rows == 1 then
                local identity, mode = '', 'FirstSubmissionWins'
                local separateLifecycle = false
                for i = 1, #rows[1][2], 2 do
                    local name, value = rows[1][2][i], rows[1][2][i+1]
                    if name == '_respire.job.id' then identity = value end
                    if name == '_respire.job.deduplication' then mode = value end
                    if name == '_respire.job.flow' or name == '_respire.job.batch'
                        or name == '_respire.job.batch_callback'
                        or name == '_respire.job.schedule' then separateLifecycle = true end
                end
                if separateLifecycle then mode = 'Separate lifecycle' end
                table.insert(result, {id, identity, mode})
            end
        end
        return result
        """);

    // Keep the five positional metadata pairs aligned with the worker. Never decode/re-encode
    // original names or values in .NET: Redis Lua strings preserve arbitrary binary fields.
    internal static readonly RespireScript Mutate = RespireScript.Create("""
        if KEYS[1] == KEYS[2] then return -1 end
        local entries = redis.call('XRANGE', KEYS[2], ARGV[2], ARGV[2], 'COUNT', 1)
        if #entries == 0 then return 0 end
        local f = entries[1][2]
        -- 2058 values = two values per pair for 1024 original fields plus five metadata fields.
        if #f < 12 or #f > 2058 or f[1] ~= '_respire.source_id'
            or not string.match(f[2], '^%d+%-%d+$') or f[3] ~= '_respire.group' or f[4] ~= ARGV[1]
            or f[5] ~= '_respire.attempt' or not string.match(f[6], '^%d+$')
            or f[7] ~= '_respire.reason' or f[9] ~= '_respire.exception_type' then return -1 end
        if ARGV[3] == 'delete' then return redis.call('XDEL', KEYS[2], ARGV[2]) end
        local groups = redis.call('XINFO', 'GROUPS', KEYS[1])
        local found = false
        for _, group in ipairs(groups) do
            for i = 1, #group, 2 do
                if group[i] == 'name' and group[i+1] == ARGV[1] then found = true end
            end
        end
        if not found then return -1 end
        local fields = {}
        for i = 11, #f, 2 do
            -- Operator retries are independent jobs: original flow and batch members are terminal,
            -- and their retained state may have been removed. Plain streams preserve these names.
            if ARGV[4] ~= '1' or (f[i] ~= '_respire.job.flow' and f[i] ~= '_respire.job.node'
                and f[i] ~= '_respire.job.batch' and f[i] ~= '_respire.job.member') then
                table.insert(fields, f[i])
                local value = f[i+1]
                if ARGV[4] == '1' and (f[i] == '_respire.job.attempts' or f[i] == '_respire.job.expires') then value = '0' end
                table.insert(fields, value)
            end
        end
        if not redis.acl_check_cmd('XADD', KEYS[1], '*', unpack(fields))
            or not redis.acl_check_cmd('XDEL', KEYS[1], '0-0')
            or not redis.acl_check_cmd('XDEL', KEYS[2], ARGV[2]) then
            return redis.error_reply('NOPERM dashboard retry is not permitted')
        end
        local added = redis.call('XADD', KEYS[1], '*', unpack(fields))
        local removed = redis.pcall('XDEL', KEYS[2], ARGV[2])
        if type(removed) == 'table' and removed.err then
            redis.call('XDEL', KEYS[1], added)
            return removed
        end
        return 1
        """);
}
