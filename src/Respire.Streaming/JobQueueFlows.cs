namespace Respire.Streaming;

internal static class JobQueueFlows
{
    internal const int MaximumResultBytes = 4096;
    internal const int MaximumInputBytes = 262144;

    private const string Helpers = """
        local function decode(data)
            local flow = cmsgpack.unpack(data)
            if flow.version ~= 1 then error('ERR unsupported job flow version') end
            return flow
        end
        local function permit(command, ...)
            if not redis.acl_check_cmd or not redis.acl_check_cmd(command, ...) then
                error('NOPERM job flows require Redis 7.0 and queue command permissions')
            end
        end
        local function checktype(key, expected)
            local kind = redis.call('TYPE', key).ok
            if kind ~= 'none' and kind ~= expected then error('WRONGTYPE job flow key') end
        end
        local function envelope(flow, index)
            local node = flow.nodes[index]
            local fields = {'_respire.job.id', node.id, '_respire.job.payload', node.payload,
                '_respire.job.flow', flow.key, '_respire.job.node', tostring(index)}
            if flow.trace ~= '' then
                table.insert(fields, 'traceparent'); table.insert(fields, flow.trace)
                if flow.state ~= '' then table.insert(fields, 'tracestate'); table.insert(fields, flow.state) end
            end
            return fields
        end

        """;

    internal static readonly RespireScript Enqueue = RespireScript.Create(Helpers + """
        local existing = redis.call('GET', KEYS[2])
        if existing then decode(existing); return 0 end
        checktype(KEYS[1], 'stream')
        permit('SET', KEYS[2], 'value')
        permit('XADD', KEYS[1], '*', 'field', 'value')
        permit('XDEL', KEYS[1], '0-0')
        local flow = {version = 1, policy = tonumber(ARGV[1]), trace = ARGV[2], state = ARGV[3], key = ARGV[4], nodes = {}}
        for i = 5, #ARGV, 3 do
            table.insert(flow.nodes, {id = ARGV[i], payload = ARGV[i+1], parent = tonumber(ARGV[i+2]),
                children = {}, remaining = 0, status = 'waiting'})
        end
        for i, node in ipairs(flow.nodes) do
            if node.parent > 0 then
                local parent = flow.nodes[node.parent]
                table.insert(parent.children, i)
                parent.remaining = parent.remaining + 1
            end
        end
        local appended = {}
        local function rollback()
            for _, id in ipairs(appended) do redis.call('XDEL', KEYS[1], id) end
        end
        for i, node in ipairs(flow.nodes) do
            if node.remaining == 0 then
                local id = redis.pcall('XADD', KEYS[1], '*', unpack(envelope(flow, i)))
                if type(id) == 'table' and id.err then rollback(); return id end
                table.insert(appended, id)
                node.status = 'ready'
            end
        end
        local saved = redis.pcall('SET', KEYS[2], cmsgpack.pack(flow))
        if type(saved) == 'table' and saved.err then rollback(); return saved end
        return 1
        """);

    // Called after the ownership fence but before any completion mutation. Calculate the
    // transition in memory, then compensate every append/state write if acknowledgement fails.
    internal const string PrepareCompletion = Helpers + """
        local flow, originalFlow, updatedFlow
        local flowSaved = false
        local activated, failedParents, flowAppends, flowEvents
        local function rollbackFlow()
            if not originalFlow then return end
            for _, id in ipairs(flowAppends) do redis.call('XDEL', KEYS[1], id) end
            for _, id in ipairs(flowEvents) do redis.call('XDEL', KEYS[2], id) end
            if flowSaved then redis.call('SET', KEYS[4], originalFlow) end
        end
        if KEYS[4] and not isBatch then
            activated, failedParents, flowAppends, flowEvents = {}, {}, {}, {}
            originalFlow = redis.call('GET', KEYS[4])
            if not originalFlow then return redis.error_reply('ERR missing job flow state') end
            flow = decode(originalFlow)
            local index
            for i = 1, #entries[1][2], 2 do
                if entries[1][2][i] == '_respire.job.node' then index = tonumber(entries[1][2][i+1]) end
            end
            local node = flow.nodes[index]
            if not node or node.id ~= job or node.status ~= 'ready' then
                return redis.error_reply('ERR invalid job flow delivery')
            end
            node.status = ARGV[5] == 'completed' and 'completed' or 'failed'
            if node.status == 'completed' and ARGV[8] == '1' then
                if #ARGV[9] > 4096 then return redis.error_reply('ERR job flow result exceeds 4096 bytes') end
                node.result = ARGV[9]
            end
            local parentIndex = node.parent
            while parentIndex > 0 do
                local parent = flow.nodes[parentIndex]
                parent.remaining = parent.remaining - 1
                if parent.remaining > 0 then break end
                local failed = false
                for _, child in ipairs(parent.children) do
                    if flow.nodes[child].status == 'failed' then failed = true end
                end
                if failed and flow.policy == 0 then
                    parent.status = 'failed'
                    table.insert(failedParents, parentIndex)
                    parentIndex = parent.parent
                else
                    parent.status = 'ready'
                    table.insert(activated, parentIndex)
                    break
                end
            end
            updatedFlow = cmsgpack.pack(flow)
            checktype(KEYS[1], 'stream')
            permit('SET', KEYS[4], originalFlow)
            permit('XADD', KEYS[1], '*', 'field', 'value')
            permit('XDEL', KEYS[1], '0-0')
        end

        """;

    internal const string CommitCompletion = """
        local function commitFlow()
            if not flow then return 1 end
            for _, index in ipairs(activated) do
                local id = redis.pcall('XADD', KEYS[1], '*', unpack(envelope(flow, index)))
                if type(id) == 'table' and id.err then return id end
                table.insert(flowAppends, id)
            end
            for _, index in ipairs(failedParents) do
                local id = redis.pcall('XADD', KEYS[2], '*', 'job_id', flow.nodes[index].id,
                    'entry_id', '0-0', 'attempt', '0', 'kind', 'dead-letter', 'reason', 'child-failed',
                    'flow_key', flow.key)
                if type(id) == 'table' and id.err then return id end
                table.insert(flowEvents, id)
            end
            local saved = redis.pcall('SET', KEYS[4], updatedFlow)
            if type(saved) == 'table' and saved.err then return saved end
            flowSaved = true
            return 1
        end
        local flowCommitted = commitFlow()
        if type(flowCommitted) == 'table' and flowCommitted.err then
            rollbackFlow()
            redis.call('XDEL', KEYS[2], event)
            if dead then redis.call('XDEL', KEYS[3], dead) end
            return flowCommitted
        end

        """;

    internal static readonly RespireScript Remove = RespireScript.Create(Helpers + """
        local data = redis.call('GET', KEYS[1])
        if not data then return 0 end
        local flow = decode(data)
        for _, node in ipairs(flow.nodes) do
            if node.status ~= 'completed' and node.status ~= 'failed' then
                return redis.error_reply('ERR cannot remove an active job flow')
            end
        end
        return redis.call('DEL', KEYS[1])
        """);

    private static readonly RespireScript Read = RespireScript.Create(Helpers + """
        local data = redis.call('GET', KEYS[1])
        if not data then return {} end
        local flow = decode(data)
        local result = {}
        local index = tonumber(ARGV[1])
        local function add(node)
            table.insert(result, {node.id, node.status, node.result or false})
        end
        if index == 0 then
            for _, node in ipairs(flow.nodes) do add(node) end
        else
            for _, child in ipairs(flow.nodes[index].children) do
                local node = flow.nodes[child]
                if flow.policy ~= 1 or node.status ~= 'failed' then add(node) end
            end
        end
        return result
        """);

    internal static async ValueTask<IReadOnlyList<RespireJobFlowResult>> ReadResultsAsync(
        IRespireClient client, string key, int node, CancellationToken token)
    {
        using var reply = await client.Scripts.ExecuteAsync(Read, [key], [node], token).ConfigureAwait(false);
        var results = new RespireJobFlowResult[reply.Count];
        for (var i = 0; i < results.Length; i++)
            results[i] = new(reply[i][0].AsString(), reply[i][1].AsString(),
                reply[i][2].IsNull ? null : reply[i][2].AsBytes());
        return results;
    }
}
