namespace Respire.Streaming;

internal static class JobQueueBatches
{
    internal const int MaximumInputBytes = 262144;

    // Prefix helpers to coexist with the flow helpers in the common completion script.
    private const string Helpers = """
        local function batchDecode(data)
            local batch = cmsgpack.unpack(data)
            if batch.version ~= 1 then error('ERR unsupported job batch version') end
            return batch
        end
        local function batchRead(key)
            local data = redis.call('GET', key)
            if not data then error('ERR missing job batch state') end
            return batchDecode(data), data
        end
        local function batchPermit(command, ...)
            if not redis.acl_check_cmd or not redis.acl_check_cmd(command, ...) then
                error('NOPERM job batches require Redis 7.0 and queue command permissions')
            end
        end
        local function batchPreflight(stream, key)
            local kind = redis.call('TYPE', stream).ok
            if kind ~= 'none' and kind ~= 'stream' then error('WRONGTYPE job batch stream') end
            batchPermit('SET', key, 'value')
            batchPermit('XADD', stream, '*', 'field', 'value')
            batchPermit('XDEL', stream, '0-0')
        end
        local function batchEnvelope(batch, job, index)
            local fields = {'_respire.job.id', job.id, '_respire.job.payload', job.payload}
            if index then
                table.insert(fields, '_respire.job.batch'); table.insert(fields, batch.key)
                table.insert(fields, '_respire.job.member'); table.insert(fields, tostring(index))
            else
                table.insert(fields, '_respire.job.batch_callback'); table.insert(fields, batch.key)
            end
            if batch.trace ~= '' then
                table.insert(fields, 'traceparent'); table.insert(fields, batch.trace)
                if batch.state ~= '' then table.insert(fields, 'tracestate'); table.insert(fields, batch.state) end
            end
            return fields
        end
        local function batchFinish(batch)
            local callbacks = {}
            if batch.sealed and batch.pending == 0 and not batch.completed then
                batch.completed = true
                local outcome = (batch.failed > 0 or batch.cancel) and 2 or 1
                if batch.callbacks[outcome].id ~= '' then table.insert(callbacks, batch.callbacks[outcome]) end
                if batch.callbacks[3].id ~= '' then table.insert(callbacks, batch.callbacks[3]) end
            end
            return callbacks
        end
        local function batchAppend(stream, fields, appended)
            local id = redis.pcall('XADD', stream, '*', unpack(fields))
            if type(id) == 'table' and id.err then return id end
            table.insert(appended, id)
            return 1
        end
        local function batchRollbackAppends(stream, appended)
            for _, id in ipairs(appended) do redis.call('XDEL', stream, id) end
        end
        local function batchSave(stream, key, batch, callbacks, appended, data)
            -- Encode before the first write, so encoding errors cannot strand callbacks.
            data = data or cmsgpack.pack(batch)
            for _, callback in ipairs(callbacks) do
                local added = batchAppend(stream, batchEnvelope(batch, callback), appended)
                if type(added) == 'table' and added.err then return added end
            end
            return redis.pcall('SET', key, data)
        end

        """;

    internal static readonly RespireScript Create = RespireScript.Create(Helpers + """
        local existing = redis.call('GET', KEYS[1])
        if existing then batchDecode(existing); return 0 end
        local batch = {version = 1, key = ARGV[1], trace = ARGV[2], state = ARGV[3],
            nodes = {}, callbacks = {}, bytes = 0, pending = 0, succeeded = 0, failed = 0, cancelled = 0,
            sealed = false, cancel = false, completed = false}
        for i = 4, 9, 2 do
            table.insert(batch.callbacks, {id = ARGV[i], payload = ARGV[i+1]})
            batch.bytes = batch.bytes + #ARGV[i] + #ARGV[i+1]
        end
        if batch.bytes > 262144 then return redis.error_reply('ERR batch exceeds 256 KiB') end
        batchPermit('SET', KEYS[1], 'value')
        redis.call('SET', KEYS[1], cmsgpack.pack(batch))
        return 1
        """);

    internal static readonly RespireScript Add = RespireScript.Create(Helpers + """
        local batch = batchRead(KEYS[2])
        for _, node in ipairs(batch.nodes) do
            if node.id == ARGV[1] then return 0 end
        end
        if batch.sealed then return redis.error_reply('ERR batch is sealed') end
        if #batch.nodes >= 256 then return redis.error_reply('ERR batch exceeds 256 members') end
        batch.bytes = batch.bytes + #ARGV[1] + #ARGV[2]
        if batch.bytes > 262144 then return redis.error_reply('ERR batch exceeds 256 KiB') end
        batchPreflight(KEYS[1], KEYS[2])
        local node = {id = ARGV[1], payload = ARGV[2], status = 'pending'}
        table.insert(batch.nodes, node)
        batch.pending = batch.pending + 1
        local data = cmsgpack.pack(batch)
        local appended = {}
        local added = batchAppend(KEYS[1], batchEnvelope(batch, node, #batch.nodes), appended)
        if type(added) == 'table' and added.err then return added end
        local saved = redis.pcall('SET', KEYS[2], data)
        if type(saved) == 'table' and saved.err then batchRollbackAppends(KEYS[1], appended); return saved end
        return 1
        """);

    internal static readonly RespireScript Seal = RespireScript.Create(Helpers + """
        local batch = batchRead(KEYS[2])
        if batch.completed then return 0 end
        batchPreflight(KEYS[1], KEYS[2])
        batch.sealed = true
        if ARGV[1] == '1' then batch.cancel = true end
        local callbacks = batchFinish(batch)
        local appended = {}
        local saved = batchSave(KEYS[1], KEYS[2], batch, callbacks, appended)
        if type(saved) == 'table' and saved.err then batchRollbackAppends(KEYS[1], appended); return saved end
        return 1
        """);

    internal static readonly RespireScript Read = RespireScript.Create(Helpers + """
        local data = redis.call('GET', KEYS[1])
        if not data then return {} end
        local batch = batchDecode(data)
        return {batch.pending, batch.succeeded, batch.failed, batch.cancelled,
            batch.sealed and 1 or 0, batch.cancel and 1 or 0, batch.completed and 1 or 0}
        """);

    internal static readonly RespireScript CheckCancellation = RespireScript.Create(Helpers + """
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[3], ARGV[3], 1)
        if #pending ~= 1 or pending[1][2] ~= ARGV[2] or tostring(pending[1][4]) ~= ARGV[4] then return -1 end
        local batch = batchRead(KEYS[2])
        return batch.cancel and 1 or 0
        """);

    internal static readonly RespireScript Remove = RespireScript.Create(Helpers + """
        local data = redis.call('GET', KEYS[1])
        if not data then return 0 end
        local batch = batchDecode(data)
        if not batch.completed then return redis.error_reply('ERR cannot remove an active job batch') end
        return redis.call('DEL', KEYS[1])
        """);

    internal const string PrepareCompletion = Helpers + """
        local batch, originalBatch, updatedBatch, batchCallbacks
        local batchSaved = false
        local batchAppends
        local function rollbackBatch()
            if not originalBatch then return end
            batchRollbackAppends(KEYS[1], batchAppends)
            if batchSaved then redis.call('SET', KEYS[4], originalBatch) end
        end
        if isBatch then
            if not KEYS[4] then return redis.error_reply('ERR missing job batch key') end
            batch, originalBatch = batchRead(KEYS[4])
            batchAppends = {}
            local index
            for i = 1, #entries[1][2], 2 do
                if entries[1][2][i] == '_respire.job.member' then index = tonumber(entries[1][2][i+1]) end
            end
            local node = batch.nodes[index]
            if not node or node.id ~= job or node.status ~= 'pending' then
                return redis.error_reply('ERR invalid job batch delivery')
            end
            node.status = batch.cancel and 'cancelled' or (ARGV[5] == 'completed' and 'succeeded' or 'failed')
            batch[node.status] = batch[node.status] + 1
            batch.pending = batch.pending - 1
            batchCallbacks = batchFinish(batch)
            updatedBatch = cmsgpack.pack(batch)
            batchPreflight(KEYS[1], KEYS[4])
        end

        """;

    internal const string CommitCompletion = """
        if batch then
            local saved = batchSave(KEYS[1], KEYS[4], batch, batchCallbacks, batchAppends, updatedBatch)
            if type(saved) == 'table' and saved.err then
                rollbackBatch()
                rollbackFlow()
                redis.call('XDEL', KEYS[2], event)
                if dead then redis.call('XDEL', KEYS[3], dead) end
                return saved
            end
            batchSaved = true
        end

        """;
}
