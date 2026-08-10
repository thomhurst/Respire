namespace Respire;

/// <summary>The result of trying to acquire a distributed lock.</summary>
public readonly struct RespireLockAttempt : IAsyncDisposable
{
    private readonly RespireLock? _lock;

    internal RespireLockAttempt(RespireLock? @lock) => _lock = @lock;

    /// <summary>Whether this attempt acquired the lock.</summary>
    public bool Acquired => _lock is not null;

    /// <summary>
    /// The acquired lock. Throws <see cref="RespireLockNotAcquiredException"/> when
    /// <see cref="Acquired"/> is <see langword="false"/>.
    /// </summary>
    public RespireLock Lock => _lock ?? throw new RespireLockNotAcquiredException();

    /// <summary>Releases the acquired lock, or does nothing when acquisition failed.</summary>
    public ValueTask DisposeAsync() => _lock?.DisposeAsync() ?? default;
}

/// <summary>
/// An acquired distributed lock. Exposed by <see cref="RespireLockAttempt.Lock"/> or returned by
/// <see cref="ILockCommands.AcquireOrThrowAsync(RespireKey, TimeSpan, CancellationToken)"/>,
/// which generates the owner token, so callers never invent or thread one through calls. Every
/// operation compares that token on the server, so a lock that expired and was taken by someone
/// else is never extended or deleted by this handle.
/// </summary>
/// <remarks>
/// The lock is a lease, not a mutex: it disappears on its own when <see cref="Expiry"/> elapses,
/// even mid-work. Keep protected work shorter than the expiry, or call
/// <see cref="ExtendAsync"/> before it elapses and stop protected writes as soon as that returns
/// <see langword="false"/>.
/// </remarks>
public sealed class RespireLock : IAsyncDisposable
{
    private const int TokenLength = 32;

    private readonly ILockCommands _locks;
    private long _expiryTicks;
    private int _released;

    internal RespireLock(ILockCommands locks, RespireKey key, ReadOnlyMemory<byte> token, TimeSpan expiry)
    {
        _locks = locks;
        Key = key.Snapshot();
        Token = token;
        _expiryTicks = expiry.Ticks;
    }

    /// <summary>The locked key, as passed to <c>AcquireAsync</c> (before any client key prefix).</summary>
    public RespireKey Key { get; }

    /// <summary>
    /// The owner token stored in the key: 32 ASCII hex characters from a <see cref="Guid"/>. Held
    /// as bytes so it compares byte-for-byte with the value the server round-trips, and with
    /// <see cref="ILockCommands.QueryAsync"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Token { get; }

    /// <summary>
    /// The expiry currently applied to the lock: the one it was acquired with, or the one from the
    /// most recent successful <see cref="ExtendAsync"/>. It counts from that call, not from now.
    /// </summary>
    public TimeSpan Expiry => TimeSpan.FromTicks(Interlocked.Read(ref _expiryTicks));

    /// <summary>
    /// Resets the lock's expiry to <paramref name="expiry"/> from now, only while this handle is
    /// still the owner. Redis: compare-and-PEXPIRE.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the lock is no longer owned — it expired, it was released, or
    /// another owner took it — in which case protected work must stop rather than retry.
    /// </returns>
    public async ValueTask<bool> ExtendAsync(TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _released) != 0)
        {
            // Released keys are owned by nobody or by the next owner; either way not by us.
            return false;
        }

        if (!await _locks.ExtendAsync(Key, Token, expiry, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Interlocked.Exchange(ref _expiryTicks, expiry.Ticks);
        return true;
    }

    /// <summary>
    /// Releases the lock, only while this handle is still the owner. Redis: compare-and-DEL.
    /// Idempotent: a second call returns <see langword="false"/> without touching the server.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call deleted the lock; <see langword="false"/> when it was
    /// already released, had expired, or is held by another owner.
    /// </returns>
    public async ValueTask<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return false;
        }

        try
        {
            return await _locks.ReleaseAsync(Key, Token, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The delete is undecided, so stay releasable: a retry (or DisposeAsync) may still
            // land, and a duplicate compare-and-DEL is harmless.
            Volatile.Write(ref _released, 0);
            throw;
        }
    }

    /// <summary>
    /// Releases the lock when <see cref="ReleaseAsync"/> has not already done so.
    /// </summary>
    /// <remarks>
    /// Failures that mean the command could not complete — a lost or disposed connection, or a
    /// command timeout — are swallowed, because disposal usually unwinds a scope that is already
    /// failing and must not replace the caller's exception with a cleanup one. The lock is not
    /// leaked by that: it expires on its own within <see cref="Expiry"/>. Errors the server
    /// answered with are not swallowed. Call <see cref="ReleaseAsync"/> explicitly when the
    /// release itself must be observed.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RespireConnectionException or RespireTimeoutException or ObjectDisposedException)
        {
            // The release could not be delivered. Swallowed so cleanup never masks the caller's
            // own failure; expiry still frees the lock, and the handle stays releasable for a
            // caller that wants to retry it explicitly.
        }
    }

    /// <summary>Generates a fresh owner token: a <see cref="Guid"/> as 32 ASCII hex bytes.</summary>
    internal static ReadOnlyMemory<byte> NewToken()
    {
        var token = new byte[TokenLength];
        Guid.NewGuid().TryFormat(token.AsSpan(), out _, "N");
        return token;
    }
}
