namespace Respire.Streaming.Dashboard;

internal static class DashboardScripts
{
    internal const int GroupStagingRequired = -3;

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
    internal static readonly RespireScript Mutate = RespireScript.Create(JobQueueScheduling.CommandHelpers + """
        local scheduled, order, grouped, parked = KEYS[3], KEYS[4], KEYS[5], KEYS[6]

        """ + JobGroupScheduling.GroupedStaging + $$"""
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
            -- Operator retries are independent jobs: original lifecycle state is terminal.
            -- Plain streams preserve these names.
            if ARGV[4] ~= '1' or (f[i] ~= '_respire.job.flow' and f[i] ~= '_respire.job.node'
                and f[i] ~= '_respire.job.batch' and f[i] ~= '_respire.job.member'
                and f[i] ~= '_respire.job.generation' and f[i] ~= '_respire.job.revision') then
                table.insert(fields, f[i])
                local value = f[i+1]
                if ARGV[4] == '1' and (f[i] == '_respire.job.attempts' or f[i] == '_respire.job.expires') then value = '0' end
                if ARGV[4] == '1' and f[i] == '_respire.job.deduplication' then value = 'Separate lifecycle' end
                table.insert(fields, value)
            end
        end
        local prefix = ARGV[4] == '1' and groupPrefix(fields)
        if prefix and not scheduled then return {{GroupStagingRequired}} end
        if not redis.acl_check_cmd('XDEL', KEYS[2], ARGV[2]) then
            return redis.error_reply('NOPERM dashboard retry is not permitted')
        end
        local added, due
        if prefix then
            checktype(scheduled, 'stream')
            permit('XADD', scheduled, '*', 'field', 'value')
            permit('XDEL', scheduled, '0-0')
            groupStagingPermissions()
            due = now()
            added = stageGrouped(scheduled, fields, prefix, due)
            if type(added) == 'table' then return added end
        else
            if not redis.acl_check_cmd('XADD', KEYS[1], '*', unpack(fields))
                or not redis.acl_check_cmd('XDEL', KEYS[1], '0-0') then
                return redis.error_reply('NOPERM dashboard retry is not permitted')
            end
            added = redis.call('XADD', KEYS[1], '*', unpack(fields))
        end
        local removed = redis.pcall('XDEL', KEYS[2], ARGV[2])
        if type(removed) == 'table' and removed.err then
            if prefix then unstageGrouped(scheduled, prefix, due, added)
            else redis.call('XDEL', KEYS[1], added) end
            return removed
        end
        return 1
        """);
}
