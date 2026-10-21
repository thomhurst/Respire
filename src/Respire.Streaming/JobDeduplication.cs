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
}

internal static class JobDeduplication
{
    internal static string Key(string stream) => stream + ":dedup-deliveries";

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
        local existing = redis.call('GET', deduplicationKey)
        if existing then
            local id, window = string.match(existing, '^(%d+%-%d+)|extend|(%d+)$')
            if id then
                redis.call('SET', deduplicationKey, existing, 'PX', window)
                return {id, 0}
            end
            local id, storedGeneration = string.match(existing, '^(%d+%-%d+)|replace|(%x+)$')
            if not id then return {existing, 0} end
            previousRevision = redis.call('HGET', lifecycleKey, storedGeneration)
            if previousRevision == revision .. '|pending' then return {id, 0} end
            -- Only a never-acquired generation is replaceable. Missing state is terminal.
            if not previousRevision or string.sub(previousRevision, -8) ~= '|pending' then return {id, 0} end
            generation = storedGeneration
            deduplicationMode = '2'
            deduplicationExpiryMode = 'PXAT'
            deduplicationWindow = tostring(redis.call('PEXPIRETIME', deduplicationKey))
        end
        local function record(id, mode, window)
            if mode == '1' then return id .. '|extend|' .. window end
            if mode == '2' then return id .. '|replace|' .. generation end
            return id
        end
        local function envelope(fields)
            if deduplicationMode == '1' then
                table.insert(fields, '_respire.job.deduplication'); table.insert(fields, 'Extend')
            elseif deduplicationMode == '2' then
                local kind = redis.call('TYPE', lifecycleKey).ok
                if kind ~= 'none' and kind ~= 'hash' then error('WRONGTYPE job deduplication lifecycle') end
                if not redis.acl_check_cmd('HSET', lifecycleKey, generation, revision .. '|pending')
                    or not redis.acl_check_cmd('HDEL', lifecycleKey, generation) then
                    error('NOPERM job replacement lifecycle is not permitted')
                end
                table.insert(fields, '_respire.job.deduplication'); table.insert(fields, 'Replace')
                table.insert(fields, '_respire.job.generation'); table.insert(fields, generation)
                table.insert(fields, '_respire.job.revision'); table.insert(fields, revision)
            end
        end
        local function persist(id)
            if deduplicationMode == '2' then
                local saved = redis.pcall('HSET', lifecycleKey, generation, revision .. '|pending')
                if type(saved) == 'table' and saved.err then return saved end
            end
            local saved = redis.pcall('SET', deduplicationKey, record(id, deduplicationMode, deduplicationWindow), deduplicationExpiryMode, deduplicationWindow)
            if type(saved) == 'table' and saved.err and deduplicationMode == '2' then
                if previousRevision then redis.call('HSET', lifecycleKey, generation, previousRevision)
                else redis.call('HDEL', lifecycleKey, generation) end
            end
            return saved
        end

        """;

    // KEYS are explicit even in Cluster. Legacy script callers without lifecycle metadata
    // need no additional key; every Replace entry must be read by an upgraded worker.
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
            if not lifecycleKey then error('ERR Replace requires an upgraded job worker') end
            local state = redis.call('HGET', lifecycleKey, generation)
            return state == revision .. '|pending' or state == revision .. '|active'
        end
        local function retire(fields)
            local generation = identity(fields)
            if generation and current(fields) then
                if not redis.acl_check_cmd('HDEL', lifecycleKey, generation) then
                    error('NOPERM job lifecycle cleanup is not permitted')
                end
                redis.call('HDEL', lifecycleKey, generation)
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
            if #rows == 1 then
                if not current(rows[1][2]) then return 0 end
                local generation = identity(rows[1][2])
                if generation and not redis.acl_check_cmd('HDEL', lifecycleKey, generation) then
                    return redis.error_reply('NOPERM job lifecycle cleanup is not permitted')
                end
            end
            local function terminal()

            """ + key.Script.Source + "\nend\nlocal result = terminal()\n"
            + (key.Expiry ? "if result == -1" : "if result == 1 or result == 2")
            + " then if #rows == 1 then retire(rows[1][2]) end end\nreturn result"));

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
        if (mode is not (RespireJobDeduplicationMode.FirstSubmissionWins or RespireJobDeduplicationMode.Extend or RespireJobDeduplicationMode.Replace))
            throw new ArgumentOutOfRangeException(nameof(mode));
    }
}
