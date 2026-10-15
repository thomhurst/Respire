using System.Diagnostics;
using System.Globalization;

namespace Respire.Coordination;

/// <summary>How a coordinated runtime settings change treats shared Redis state.</summary>
public enum RespireRateLimitUpdateMode
{
    /// <summary>Migrate shared usage once, on the elected updater, before publishing local settings.</summary>
    Migrate,
    /// <summary>Publish local settings after another process has successfully migrated shared usage.</summary>
    Adopt,
}

internal sealed partial class RedisRateLimiter
{
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private TaskCompletionSource? _settingsChange;
    private TaskCompletionSource? _readersDrained;
    private int _redisReaders;
    private long _settingsVersion;

    // A reader holds the entire settings set through the Redis reply. Readers remain concurrent;
    // only updates close admission and wait for existing attempts to finish.
    private async ValueTask EnterRedisAttemptAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_queueGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_settingsChange is null)
                {
                    _redisReaders++;
                    return;
                }
                changed = _settingsChange.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ExitRedisAttempt()
    {
        lock (_queueGate)
        {
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
            if (--_redisReaders == 0) _readersDrained?.TrySetResult();
        }
    }

    internal ValueTask UpdateAsync(IReadOnlyList<RespireRateLimit> settings, bool composite,
        RespireRateLimitUpdateMode mode, CancellationToken cancellationToken)
        => CoordinationCommandHooks.RunAsync(_coordination.Client, "RATE LIMIT UPDATE", cancellationToken,
            (Limiter: this, Settings: settings, Composite: composite, Mode: mode, Token: cancellationToken),
            static s => s.Limiter.UpdateCoreAsync(s.Settings, s.Composite, s.Mode, s.Token));

    private async ValueTask UpdateCoreAsync(IReadOnlyList<RespireRateLimit> settings, bool composite,
        RespireRateLimitUpdateMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (mode is not RespireRateLimitUpdateMode.Migrate and not RespireRateLimitUpdateMode.Adopt)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (composite != (_kind == RedisRateLimiterKind.Composite))
            throw new ArgumentException("Use the single-definition overload for standalone limiters and the list overload for composites.", nameof(settings));
        var definitions = composite ? SnapshotLimits(settings) : settings.ToArray();
        if (!composite && definitions[0].Kind != _kind)
            throw new ArgumentException("An update cannot change the limiter algorithm.", nameof(settings));
        var configurations = definitions.Select(static definition => definition.Configuration).ToArray();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _activeAcquisitions);
        var migrationStarted = false;
        try
        {
            Task drained;
            string[] previous;
            lock (_queueGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                previous = GetConfigurations();
                if (previous.Length != definitions.Length)
                    throw new ArgumentException("An update cannot change the number of composite components.", nameof(settings));
                for (var i = 0; i < previous.Length; i++)
                    if (previous[i][0] != configurations[i][0])
                        throw new ArgumentException("An update cannot change component algorithms or their order.", nameof(settings));
                if (previous.SequenceEqual(configurations)) return;
                _settingsChange = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _readersDrained = _redisReaders == 0 ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _readersDrained?.Task ?? Task.CompletedTask;
            }
            await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (mode == RespireRateLimitUpdateMode.Migrate)
            {
                var args = new RespireValue[2 + previous.Length * 2];
                args[0] = composite ? 1 : 0;
                args[1] = previous.Length;
                for (var i = 0; i < previous.Length; i++)
                {
                    args[2 + i] = previous[i];
                    args[2 + previous.Length + i] = configurations[i];
                }
                migrationStarted = true;
                // Cancellation is honored before submission. Once submitted, observe the migration
                // reply so cancellation cannot knowingly leave local and Redis settings out of sync.
                using var result = await _coordination.ExecuteCompositeRateLimitAsync(
                    UpdateScript, _key, args, CancellationToken.None).ConfigureAwait(false);
                if (result.AsInteger() != 1) throw new RespireProtocolException("Rate-limit migration returned an invalid response.");
            }
            lock (_queueGate)
            {
                _permitLimit = definitions.Min(static definition => definition.PermitLimit);
                if (composite)
                {
                    _compositeArguments = new RespireValue[1 + definitions.Length];
                    for (var i = 0; i < definitions.Length; i++) _compositeArguments[i + 1] = configurations[i];
                }
                else
                {
                    var definition = definitions[0];
                    _periodMs = definition.PeriodMs;
                    _segments = _kind == RedisRateLimiterKind.SlidingWindow ? definition.Extra : 0;
                    _tokensPerPeriod = _kind == RedisRateLimiterKind.TokenBucket ? definition.Extra : 0;
                    _tokenBucketExpiryMs = _kind == RedisRateLimiterKind.TokenBucket
                        ? _periodMs * (((long)_permitLimit + _tokensPerPeriod - 1) / _tokensPerPeriod) : 0;
                }
                _settingsVersion++;
                for (var node = _queue.First; node is not null;)
                {
                    var next = node.Next;
                    var request = node.Value;
                    request.SetRetryAfter(TimeSpan.Zero);
                    if (!request.IsProcessing && request.PermitCount > _permitLimit)
                        CompleteQueued(request, new RedisRateLimitLease(false, null));
                    node = next;
                }
            }
        }
        catch (RespireServerException error) when (error.Message is
            "ERR composite rate-limit configuration mismatch during update" or
            "ERR migrated GCRA debt exceeds the TimeSpan range")
        {
            // These explicit validation replies precede every write. Other server errors can
            // follow a partial script write (Redis does not roll scripts back), so fail closed.
            throw;
        }
        catch
        {
            if (migrationStarted)
            {
                // A lost reply can mean Redis migrated successfully. Fail closed instead of admitting
                // requests with an unknown configuration; the caller must reconcile shared state.
                _disposed = true;
                FailQueuedRequests(new InvalidOperationException("Rate-limit migration failed; reconcile Redis state before creating a replacement limiter."));
            }
            throw;
        }
        finally
        {
            lock (_queueGate)
            {
                _settingsChange?.TrySetResult();
                _settingsChange = null;
                _readersDrained = null;
            }
            SignalQueueChanged();
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _activeAcquisitions);
            _updateGate.Release();
        }
    }

    private string[] GetConfigurations()
    {
        if (_kind == RedisRateLimiterKind.Composite)
            return _compositeArguments!.Skip(1).Select(static value => value.ToString()).ToArray();
        var extra = _kind == RedisRateLimiterKind.SlidingWindow ? _segments : _tokensPerPeriod;
        return [string.Create(CultureInfo.InvariantCulture, $"{(int)_kind},{_permitLimit},{_periodMs},{extra}")];
    }

    // Read/validate/compute before the first write. This script runs with local acquisitions drained;
    // the application coordinates other processes, electing exactly one Migrate caller per change.
    private static readonly RespireScript UpdateScript = RespireScript.Create("""
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
        local composite = ARGV[1] == '1'
        local count = tonumber(ARGV[2])
        local clock = redis.call('TIME')
        local now = clock[1] * 1000 + math.floor(clock[2] / 1000)
        if redis.call('EXISTS', KEYS[1]) == 0 then return 1 end
        local expiry = 1
        local updates = {}
        if composite then
            local oldSignature = 'v1|' .. table.concat(ARGV, '|', 3, 2 + count)
            if redis.call('HGET', KEYS[1], 'config') ~= oldSignature then
                return redis.error_reply('ERR composite rate-limit configuration mismatch during update')
            end
            updates = {'config', 'v1|' .. table.concat(ARGV, '|', 3 + count)}
        end
        local standaloneState
        for i = 1, count do
            local old = decode(ARGV[2 + i])
            local new = decode(ARGV[2 + count + i])
            local kind, limit, period, extra = unpack(new)
            local state = {}
            if composite then state = decode(redis.call('HGET', KEYS[1], tostring(i))) end
            if kind == 0 then
                -- Existing windows keep their deadline and count. The new width starts next window.
                if composite then
                    if (state[1] or 0) <= now then state = {} end
                    expiry = math.max(expiry, (state[1] or now) - now)
                -- Standalone fixed windows retain their TTL; do not refresh it with PEXPIRE.
                else return 1 end
            elseif kind == 1 then
                if not composite then
                    local entries = redis.call('ZRANGE', KEYS[1], 0, -1, 'WITHSCORES')
                    for j = 1, #entries, 2 do
                        local separator = string.find(entries[j], ':', 1, true)
                        state[#state + 1] = tonumber(entries[j + 1])
                        state[#state + 1] = tonumber(string.sub(entries[j], separator + 1))
                    end
                end
                local active = {}
                for j = 1, #state, 2 do
                    -- Never resurrect usage that expired under the old settings.
                    if state[j] + old[3] > now and state[j] + period > now then
                        active[#active + 1] = state[j]
                        active[#active + 1] = state[j + 1]
                        expiry = math.max(expiry, state[j] + period - now)
                    end
                end
                state = active
            elseif kind == 2 then
                if not composite then
                    state = {tonumber(redis.call('HGET', KEYS[1], 'time')), tonumber(redis.call('HGET', KEYS[1], 'tokens'))}
                end
                local last, tokens = state[1] or now, state[2] or old[2]
                tokens = math.min(old[2], tokens + math.floor(math.max(0, now - last) / old[3]) * old[4])
                -- Settle old whole periods, clamp capacity, and begin a new refill period now.
                state = {now, math.min(limit, tokens)}
                expiry = math.max(expiry, math.ceil(limit / extra) * period)
            else
                local debt = math.max(0, tonumber(redis.call('GET', KEYS[1])) - now)
                -- Preserve outstanding permit debt (including a partial permit), rounded conservatively.
                local newDebt = math.ceil(debt / old[3] * period)
                if newDebt > 922337203685477 then
                    return redis.error_reply('ERR migrated GCRA debt exceeds the TimeSpan range')
                end
                state = {now + newDebt}
                expiry = math.max(1, newDebt)
            end
            if composite then
                updates[#updates + 1] = tostring(i)
                updates[#updates + 1] = encode(state)
            else standaloneState = state end
        end
        if composite then
            redis.call('HSET', KEYS[1], unpack(updates))
        else
            local kind = decode(ARGV[3])[1]
            if kind == 1 then
                -- Keep the original segment-end scores; future segments may have a different width.
                redis.call('DEL', KEYS[1])
                for j = 1, #standaloneState, 2 do
                    redis.call('ZADD', KEYS[1], standaloneState[j], encode({standaloneState[j]}) .. ':' .. encode({standaloneState[j + 1]}))
                end
            elseif kind == 2 then
                redis.call('HSET', KEYS[1], 'time', encode({standaloneState[1]}), 'tokens', encode({standaloneState[2]}))
            else redis.call('SET', KEYS[1], encode(standaloneState)) end
        end
        redis.call('PEXPIRE', KEYS[1], string.format('%.0f', expiry))
        return 1
        """);
}
