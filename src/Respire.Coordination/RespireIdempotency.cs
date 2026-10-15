using Respire.Protocol;

namespace Respire.Coordination;

/// <summary>The outcome of an atomic idempotency claim.</summary>
public enum RespireIdempotencyState
{
    /// <summary>This caller owns the new claim.</summary>
    Started,
    /// <summary>Another caller owns a live claim with the same fingerprint.</summary>
    InProgress,
    /// <summary>The first owner's result is available.</summary>
    Completed,
    /// <summary>The key is already associated with a different fingerprint.</summary>
    FingerprintMismatch,
}

/// <summary>An idempotency claim outcome and, when completed, an owned copy of the stored bytes.</summary>
public sealed class RespireIdempotencyResult
{
    internal RespireIdempotencyResult(RespireIdempotencyState state, string? ownerToken = null, byte[]? result = null)
        => (State, OwnerToken, Result) = (state, ownerToken, result ?? ReadOnlyMemory<byte>.Empty);

    /// <summary>The outcome of this attempt.</summary>
    public RespireIdempotencyState State { get; }
    /// <summary>The secret completion/abandon token, present only for <see cref="RespireIdempotencyState.Started"/>.</summary>
    public string? OwnerToken { get; }
    /// <summary>The binary result when completed; otherwise empty. Empty completed results are valid.</summary>
    public ReadOnlyMemory<byte> Result { get; }
}

/// <summary>Atomic expiring idempotency claims and binary result replay using a caller-owned Redis client.</summary>
/// <remarks>
/// Reserve a key namespace for this primitive. Expiry, eviction, or Redis data loss permits execution again;
/// this is not an exactly-once transaction with external side effects. The client is not disposed by this object.
/// </remarks>
public sealed class RespireIdempotency
{
    private readonly IRespireClient _client;

    private static readonly RespireScript Begin = RespireScript.Create("""
        if redis.call('EXISTS', KEYS[1]) == 0 then
            redis.call('HSET', KEYS[1], 'state', 'pending', 'owner', ARGV[1], 'fingerprint', ARGV[2], 'ttl', ARGV[3])
            redis.call('PEXPIRE', KEYS[1], ARGV[3])
            return {0}
        end
        local state = redis.call('HGET', KEYS[1], 'state')
        if state ~= 'pending' and state ~= 'completed' then
            return redis.error_reply('ERR invalid idempotency state')
        end
        if redis.call('HGET', KEYS[1], 'fingerprint') ~= ARGV[2] then return {3} end
        if state == 'completed' then
            local result = redis.call('HGET', KEYS[1], 'result')
            if not result then return redis.error_reply('ERR missing idempotency result') end
            return {2, result}
        end
        return {1}
        """);

    private static readonly RespireScript Complete = RespireScript.Create("""
        if redis.call('HGET', KEYS[1], 'state') ~= 'pending' then return 0 end
        if redis.call('HGET', KEYS[1], 'owner') ~= ARGV[1] then return 0 end
        local ttl = redis.call('HGET', KEYS[1], 'ttl')
        redis.call('HSET', KEYS[1], 'state', 'completed', 'result', ARGV[2])
        redis.call('HDEL', KEYS[1], 'owner')
        redis.call('PEXPIRE', KEYS[1], ttl)
        return 1
        """);

    private static readonly RespireScript Abandon = RespireScript.Create("""
        if redis.call('HGET', KEYS[1], 'state') ~= 'pending' then return 0 end
        if redis.call('HGET', KEYS[1], 'owner') ~= ARGV[1] then return 0 end
        return redis.call('DEL', KEYS[1])
        """);

    /// <summary>Creates an idempotency store without taking ownership of the client.</summary>
    public RespireIdempotency(IRespireClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>Claims an absent key, or returns its pending/completed state without extending its expiry.</summary>
    /// <param name="key">A nonempty key before the client's configured prefix.</param>
    /// <param name="ttl">At least one millisecond. Whole milliseconds bound the pending claim and, from completion, the saved result.</param>
    /// <param name="fingerprint">An application-defined request digest. Null and empty are distinct; all callers must supply the same value.</param>
    /// <param name="cancellationToken">Cancels the wait; a command already accepted by Redis may still take effect.</param>
    public ValueTask<RespireIdempotencyResult> TryBeginAsync(RespireKey key, TimeSpan ttl,
        string? fingerprint = null, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var milliseconds = ttl.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be at least one millisecond.");
        cancellationToken.ThrowIfCancellationRequested();
        return CoordinationCommandHooks.RunAsync(_client, "IDEMPOTENCY BEGIN", cancellationToken,
            (Store: this, Key: key.Snapshot(), Milliseconds: milliseconds, Fingerprint: fingerprint, Token: cancellationToken),
            static s => s.Store.BeginAsync(s.Key, s.Milliseconds, s.Fingerprint, s.Token));
    }

    private async ValueTask<RespireIdempotencyResult> BeginAsync(RespireKey key, long milliseconds,
        string? fingerprint, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        using var result = await CoordinationCommandHooks.ExecuteAsync(_client, Begin, [key],
            [owner, fingerprint is null ? "0" : "1" + fingerprint, milliseconds], cancellationToken).ConfigureAwait(false);
        if (result.Type != RespireDataType.Array || result.Count == 0 || result[0].Type != RespireDataType.Integer)
            throw new RespireProtocolException("Redis returned an invalid idempotency outcome.");
        return result[0].AsInteger() switch
        {
            0 when result.Count == 1 => new(RespireIdempotencyState.Started, owner),
            1 when result.Count == 1 => new(RespireIdempotencyState.InProgress),
            2 when result.Count == 2 && result[1].Type == RespireDataType.BulkString => new(RespireIdempotencyState.Completed, result: result[1].AsBytes()),
            3 when result.Count == 1 => new(RespireIdempotencyState.FingerprintMismatch),
            _ => throw new RespireProtocolException("Redis returned an invalid idempotency outcome."),
        };
    }

    /// <summary>Saves a binary result only for the current pending owner, restarting the original TTL from completion.</summary>
    /// <remarks>Returns false for an expired, replaced, abandoned, or already completed claim. Repeated completion never overwrites the first result. Input bytes are copied before asynchronous work.</remarks>
    public ValueTask<bool> CompleteAsync(RespireKey key, string ownerToken, ReadOnlyMemory<byte> result,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CoordinationCommandHooks.RunAsync(_client, "IDEMPOTENCY COMPLETE", cancellationToken,
            (Store: this, Key: key.Snapshot(), Owner: ownerToken, Result: result.ToArray(), Token: cancellationToken),
            static s => s.Store.ChangeAsync(Complete, s.Key, [s.Owner, s.Result], s.Token));
    }

    /// <summary>Removes only the current pending owner's claim so another caller can retry immediately.</summary>
    /// <remarks>Use only when retrying the work is safe. Returns false if the claim expired, was replaced, or completed.</remarks>
    public ValueTask<bool> AbandonAsync(RespireKey key, string ownerToken, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CoordinationCommandHooks.RunAsync(_client, "IDEMPOTENCY ABANDON", cancellationToken,
            (Store: this, Key: key.Snapshot(), Owner: ownerToken, Token: cancellationToken),
            static s => s.Store.ChangeAsync(Abandon, s.Key, [s.Owner], s.Token));
    }

    private async ValueTask<bool> ChangeAsync(RespireScript script, RespireKey key, RespireValue[] arguments,
        CancellationToken cancellationToken)
    {
        using var result = await CoordinationCommandHooks.ExecuteAsync(_client, script, [key], arguments, cancellationToken)
            .ConfigureAwait(false);
        return result.AsInteger() switch
        {
            0 => false,
            1 => true,
            _ => throw new RespireProtocolException("Redis returned an invalid idempotency transition outcome."),
        };
    }

    private static void ValidateKey(RespireKey key)
    {
        if (key.IsEmpty) throw new ArgumentException("The idempotency key must not be empty.", nameof(key));
    }
}
