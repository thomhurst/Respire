using System.Collections.Concurrent;

namespace Respire.Streaming;

/// <summary>How duplicate submissions affect a job within its deduplication window.</summary>
public enum RespireJobDeduplicationMode
{
    /// <summary>Keep the first payload and options without extending the original window.</summary>
    FirstSubmissionWins,
    /// <summary>Keep the first payload and options and renew its original window on every duplicate.</summary>
    Extend,
    /// <summary>Replace payload and options until first acquisition; preserve acquired work, including retries.</summary>
    Replace,
    /// <summary>Keep queued work; while acquired, retain one follow-up with the latest payload and options.</summary>
    KeepLastIfActive,
}

internal static class JobDeduplication
{
    internal static string Key(string stream) => stream + ":dedup-deliveries";

    // Inspect a single server-side snapshot. A client-side scan can mistake a promoted
    // or retried envelope for a missing one. Absence cannot prove an active handler ended.
    internal static readonly RespireScript ReclaimOrphans = RespireScript.Create("""
        local limit = tonumber(ARGV[1])
        local examined = redis.call('HLEN', KEYS[3])
        local function withinLimit()
            if examined > limit then error('ERR job deduplication maintenance scan limit exceeded') end
        end
        withinLimit()
        local values = redis.call('HGETALL', KEYS[3])
        local candidates = {}
        for i = 1, #values, 2 do
            local field, state = values[i], values[i+1]
            local generation = string.match(field, '^(%x+)$') or string.match(field, '^(%x+):next$')
            local revision = string.match(state, '^(%x+)|pending$')
            if generation and #generation == 32 and revision and #revision == 32 then
                candidates[field] = revision
            end
        end
        if not next(candidates) then return 0 end
        for stream = 1, 2 do
            local start = '-'
            while true do
                local rows = redis.call('XRANGE', KEYS[stream], start, '+', 'COUNT', math.min(256, limit - examined + 1))
                examined = examined + #rows
                withinLimit()
                if #rows == 0 then break end
                for _, row in ipairs(rows) do
                    local generation, revision
                    for i = 1, #row[2], 2 do
                        if row[2][i] == '_respire.job.generation' then generation = row[2][i+1] end
                        if row[2][i] == '_respire.job.revision' then revision = row[2][i+1] end
                    end
                    if generation and revision then
                        if candidates[generation] == revision then candidates[generation] = nil end
                        local followup = generation .. ':next'
                        if candidates[followup] == revision then candidates[followup] = nil end
                    end
                end
                start = '(' .. rows[#rows][1]
            end
        end
        -- All reads and the permission check precede the first mutation: Lua errors
        -- do not roll back earlier writes. Unknown metadata and active fields survive.
        local firstOrphan = next(candidates)
        if firstOrphan and not redis.acl_check_cmd('HDEL', KEYS[3], firstOrphan) then
            return redis.error_reply('NOPERM job deduplication maintenance is not permitted')
        end
        local removed = 0
        for field in pairs(candidates) do removed = removed + redis.call('HDEL', KEYS[3], field) end
        return removed
        """);

    // Default records retain their original stream-ID-only format. Extended records carry
    // the winning window, independent of the stream entry's promotion, retry or deletion.
    // Both enqueue scripts (including EXEC) execute this before any append.
    // Extend hits renew with one SET: a denied renewal leaves the record unchanged,
    // and a successful renewal returns without staging or appending anything to roll back.
    internal const string Duplicate = """
        local lifecycleKey = KEYS[#KEYS]
        local generation = revision
        local deduplicationWindow = ARGV[3]
        local deduplicationExpiryMode = 'PX'
        local previousRevision
        local followup = false
        local stateField
        local existing = redis.call('GET', deduplicationKey)
        if existing then
            local id, window = string.match(existing, '^(%d+%-%d+)|extend|(%d+)$')
            if id then
                redis.call('SET', deduplicationKey, existing, 'PX', window)
                return {id, 0}
            end
            local id, storedGeneration = string.match(existing, '^(%d+%-%d+)|replace|(%x+)$')
            local keep = false
            if not id then
                id, storedGeneration = string.match(existing, '^(%d+%-%d+)|keep|(%x+)$')
                keep = id ~= nil
            end
            if not id then return {existing, 0} end
            local state = redis.call('HGET', lifecycleKey, storedGeneration)
            if keep then
                if not state or string.sub(state, -7) ~= '|active' then return {id, 0} end
                followup = true
            elseif not state or string.sub(state, -8) ~= '|pending' then return {id, 0} end
            stateField = storedGeneration .. (followup and ':next' or '')
            previousRevision = redis.call('HGET', lifecycleKey, stateField)
            if previousRevision == revision .. '|pending' then return {id, 0} end
            generation = storedGeneration
            deduplicationMode = keep and '3' or '2'
            deduplicationExpiryMode = 'PXAT'
            deduplicationWindow = tostring(redis.call('PEXPIRETIME', deduplicationKey))
        end
        local function record(id, mode, window)
            if mode == '1' then return id .. '|extend|' .. window end
            if mode == '2' then return id .. '|replace|' .. generation end
            if mode == '3' then return id .. '|keep|' .. generation end
            return id
        end
        local function envelope(fields)
            if deduplicationMode == '1' then
                table.insert(fields, '_respire.job.deduplication'); table.insert(fields, 'Extend')
            elseif deduplicationMode == '2' or deduplicationMode == '3' then
                stateField = stateField or generation
                local kind = redis.call('TYPE', lifecycleKey).ok
                if kind ~= 'none' and kind ~= 'hash' then error('WRONGTYPE job deduplication lifecycle') end
                if not redis.acl_check_cmd('HSET', lifecycleKey, stateField, revision .. '|pending')
                    or not redis.acl_check_cmd('HDEL', lifecycleKey, stateField) then
                    error('NOPERM job replacement lifecycle is not permitted')
                end
                table.insert(fields, '_respire.job.deduplication'); table.insert(fields, deduplicationMode == '3' and 'KeepLastIfActive' or 'Replace')
                table.insert(fields, '_respire.job.generation'); table.insert(fields, generation)
                table.insert(fields, '_respire.job.revision'); table.insert(fields, revision)
            end
        end
        local function persist(id, immediateFollowup)
            local previousFollowups = immediateFollowup and redis.call('HGET', lifecycleKey, 'followups')
            if stateField then
                local saved
                if immediateFollowup then
                    saved = redis.pcall('HSET', lifecycleKey, stateField, revision .. '|pending', 'followups', '1')
                else saved = redis.pcall('HSET', lifecycleKey, stateField, revision .. '|pending') end
                if type(saved) == 'table' and saved.err then return saved end
            end
            local saved = redis.pcall('SET', deduplicationKey, record(id, deduplicationMode, deduplicationWindow), deduplicationExpiryMode, deduplicationWindow)
            if type(saved) == 'table' and saved.err and stateField then
                if previousRevision then redis.call('HSET', lifecycleKey, stateField, previousRevision)
                else redis.call('HDEL', lifecycleKey, stateField) end
                if immediateFollowup and not previousFollowups then redis.call('HDEL', lifecycleKey, 'followups') end
            end
            return saved
        end

        """;

    // KEYS are explicit even in Cluster. Legacy script callers without lifecycle metadata
    // need no additional key; tracked modes must be read by an upgraded worker.
    internal const string Lifecycle = """
        local function identity(fields)
            local generation, revision
            for i = 1, #fields, 2 do
                if fields[i] == '_respire.job.generation' then generation = fields[i+1] end
                if fields[i] == '_respire.job.revision' then revision = fields[i+1] end
            end
            return generation, revision
        end
        local function current(fields)
            local generation, revision = identity(fields)
            if not generation then return true end
            if not lifecycleKey then error('ERR tracked deduplication requires an upgraded job worker') end
            local state = redis.call('HGET', lifecycleKey, generation)
            return state == revision .. '|pending' or state == revision .. '|active'
        end
        local function retire(fields)
            local generation, revision = identity(fields)
            if generation and current(fields) then
                local next = redis.call('HGET', lifecycleKey, generation .. ':next')
                if not redis.acl_check_cmd('HDEL', lifecycleKey, generation, generation .. ':next')
                    or (next and not redis.acl_check_cmd('HSET', lifecycleKey, generation, next)) then
                    error('NOPERM job lifecycle cleanup is not permitted')
                end
                if next then
                    redis.call('HSET', lifecycleKey, generation, next)
                    redis.call('HDEL', lifecycleKey, generation .. ':next')
                else redis.call('HDEL', lifecycleKey, generation) end
            elseif generation and redis.call('HGET', lifecycleKey, generation .. ':next') == revision .. '|pending' then
                redis.call('HDEL', lifecycleKey, generation .. ':next')
            end
        end
        local function blocked(fields)
            local generation, revision = identity(fields)
            return generation and redis.call('HGET', lifecycleKey, generation .. ':next') == revision .. '|pending'
        end
        local function retirementPermissions(fields)
            local generation = identity(fields)
            if not generation then return end
            local next = redis.call('HGET', lifecycleKey, generation .. ':next')
            if not redis.acl_check_cmd('HDEL', lifecycleKey, generation, generation .. ':next')
                or (next and not redis.acl_check_cmd('HSET', lifecycleKey, generation, next)) then
                error('NOPERM job lifecycle cleanup is not permitted')
            end
        end

        """;

    private static readonly ConcurrentDictionary<(RespireScript Script, bool Expiry), RespireScript> Terminals = new();

    internal static RespireScript Terminal(RespireScript script, bool expiry = false)
        => Terminals.GetOrAdd((script, expiry), static key => RespireScript.Create("""
            local lifecycleKey = KEYS[#KEYS]
            KEYS[#KEYS] = nil

            """ + Lifecycle + """
            local rows = redis.call('XRANGE', KEYS[1], ARGV[3], ARGV[3])
            local generation, previousState, previousNext
            if #rows == 1 then
                if not current(rows[1][2]) then return 0 end
                retirementPermissions(rows[1][2])
                generation = identity(rows[1][2])
                if generation then
                    previousState = redis.call('HGET', lifecycleKey, generation)
                    previousNext = redis.call('HGET', lifecycleKey, generation .. ':next')
                    -- Allocate the successor state before acknowledgement. No client can
                    -- observe it until this script finishes; failed/stale owners restore it.
                    if previousNext then retire(rows[1][2]) end
                end
            end
            local function terminal()

            """ + key.Script.Source + "\nend\nlocal ok, result = pcall(terminal)\n"
            + (key.Expiry ? "local completed = ok and result == -1\n" : "local completed = ok and (result == 1 or result == 2)\n")
            + """
            if completed and #rows == 1 and not previousNext then retire(rows[1][2]) end
            if not completed and previousNext then
                redis.call('HSET', lifecycleKey, generation, previousState)
                redis.call('HSET', lifecycleKey, generation .. ':next', previousNext)
            end
            if not ok then
                if type(result) == 'table' and result.err then return result end
                return redis.error_reply(result)
            end
            return result
            """));

    // Called inside the reader script, after XREADGROUP/XCLAIM, before returning a
    // delivery. Producers cannot interleave with this pending -> active transition.
    internal const string Acquire = """
        local accepted = {}
        for _, entry in ipairs(page[2]) do
            local generation, revision = identity(entry[2])
            if generation then
                if not redis.acl_check_cmd('HSET', lifecycleKey, generation, revision .. '|active')
                    or not redis.acl_check_cmd('XACK', KEYS[1], ARGV[1], entry[1]) then
                    error('NOPERM job acquisition lifecycle is not permitted')
                end
                if current(entry[2]) then
                    redis.call('HSET', lifecycleKey, generation, revision .. '|active')
                    table.insert(accepted, entry)
                else redis.call('XACK', KEYS[1], ARGV[1], entry[1]) end
            else table.insert(accepted, entry) end
        end
        page[2] = accepted

        """;

    internal static void Validate(RespireJobDeduplicationMode mode)
    {
        if (mode is not (RespireJobDeduplicationMode.FirstSubmissionWins or RespireJobDeduplicationMode.Extend or RespireJobDeduplicationMode.Replace or RespireJobDeduplicationMode.KeepLastIfActive))
            throw new ArgumentOutOfRangeException(nameof(mode));
    }
}
