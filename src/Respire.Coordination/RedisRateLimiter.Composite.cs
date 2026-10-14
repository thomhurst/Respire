using System.Threading.RateLimiting;

namespace Respire.Coordination;

internal sealed partial class RedisRateLimiter
{
    private RespireValue[]? _compositeArguments;

    internal RedisRateLimiter(RespireCoordination coordination, RespireKey key,
        IReadOnlyList<RespireRateLimit> limits, int queueLimit, QueueProcessingOrder queueOrder)
        : this(coordination, key, SnapshotLimits(limits), queueLimit, queueOrder)
    {
    }

    private RedisRateLimiter(RespireCoordination coordination, RespireKey key,
        RespireRateLimit[] limits, int queueLimit, QueueProcessingOrder queueOrder)
        : this(coordination, key, limits.Min(static limit => limit.PermitLimit), queueLimit, queueOrder,
            RedisRateLimiterKind.Composite, TimeSpan.FromMilliseconds(1))
    {
        _compositeArguments = new RespireValue[1 + limits.Length];
        for (var i = 0; i < limits.Length; i++) _compositeArguments[1 + i] = limits[i].Configuration;
    }

    private static RespireRateLimit[] SnapshotLimits(IReadOnlyList<RespireRateLimit> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Count is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(limits), "Specify between one and 32 component limits.");
        var snapshot = new RespireRateLimit[limits.Count];
        for (var i = 0; i < snapshot.Length; i++)
        {
            snapshot[i] = limits[i] ?? throw new ArgumentException("Component limits must not be null.", nameof(limits));
            if (snapshot[i].Kind == RedisRateLimiterKind.Gcra)
                throw new ArgumentException("GCRA is not a composite component.", nameof(limits));
        }
        return snapshot;
    }

    private RespireValue[] CreateCompositeArguments(int permitCount)
    {
        var args = (RespireValue[])_compositeArguments!.Clone();
        args[0] = permitCount;
        return args;
    }

    // One hash, with a configuration signature and one packed integer list per component. Decimal
    // formatting preserves millisecond timestamps beyond Lua/cjson's default string precision.
    // All reads and calculations precede writes; rejection and zero-permit probes leave state untouched.
    private static readonly RespireScript CompositeScript = RespireScript.Create("""
        local function decode(value)
            local values = {}
            if value then
                for part in string.gmatch(value, '[^,]+') do values[#values + 1] = tonumber(part) end
            end
            return values
        end
        local function encode(values)
            local parts = {}
            for i, value in ipairs(values) do parts[i] = string.format('%.0f', value) end
            return table.concat(parts, ',')
        end
        local signature = 'v1|' .. table.concat(ARGV, '|', 2)
        local stored = redis.call('HGET', KEYS[1], 'config')
        if stored and stored ~= signature then
            return redis.error_reply('ERR composite rate-limit configuration mismatch')
        end
        local clock = redis.call('TIME')
        local now = clock[1] * 1000 + math.floor(clock[2] / 1000)
        local requested = tonumber(ARGV[1])
        local required = math.max(1, requested)
        local retry = 0
        local available = math.huge
        local expiry = 1
        local updates = {'config', signature}
        for i = 2, #ARGV do
            local config = decode(ARGV[i])
            local kind, limit, period, extra = unpack(config)
            local field = tostring(i - 1)
            local state = decode(redis.call('HGET', KEYS[1], field))
            local free, delay, horizon = 0, 0, 1
            if kind == 0 then
                local deadline, count = state[1] or now, state[2] or 0
                if deadline <= now then deadline, count = now + period, 0 end
                free = limit - count
                if free < required then delay = deadline - now end
                state = {deadline, count + requested}
                horizon = deadline - now
            elseif kind == 1 then
                local segment = math.ceil(period / extra)
                local bucketEnd = (math.floor(now / segment) + 1) * segment
                local active, count = {}, 0
                for j = 1, #state, 2 do
                    if state[j] + period > now then
                        active[#active + 1] = state[j]
                        active[#active + 1] = state[j + 1]
                        count = count + state[j + 1]
                    end
                end
                free = limit - count
                local needed = required - free
                if needed > 0 then
                    for j = 1, #active, 2 do
                        needed = needed - active[j + 1]
                        if needed <= 0 then delay = active[j] + period - now; break end
                    end
                end
                -- A changed segment width can place the current bucket before an older bucket.
                local position = #active + 1
                while position > 1 and active[position - 2] >= bucketEnd do position = position - 2 end
                if active[position] == bucketEnd then active[position + 1] = active[position + 1] + requested
                else
                    table.insert(active, position, bucketEnd)
                    table.insert(active, position + 1, requested)
                end
                state = active
                horizon = active[#active - 1] + period - now
            else
                local last, tokens = state[1] or now, state[2] or limit
                local elapsedPeriods = math.floor(math.max(0, now - last) / period)
                tokens = math.min(limit, tokens + elapsedPeriods * extra)
                last = last + elapsedPeriods * period
                free = tokens
                if free < required then
                    delay = math.ceil((required - tokens) / extra) * period - (now - last)
                end
                state = {last, tokens - requested}
                horizon = math.ceil(limit / extra) * period
            end
            available = math.min(available, free)
            retry = math.max(retry, delay)
            expiry = math.max(expiry, horizon)
            updates[#updates + 1] = field
            updates[#updates + 1] = encode(state)
        end
        if retry > 0 then return {0, retry, available} end
        if requested == 0 then return {1, 0, available} end
        redis.call('HSET', KEYS[1], unpack(updates))
        redis.call('PEXPIRE', KEYS[1], string.format('%.0f', expiry))
        return {1, 0, available - requested}
        """);
}
