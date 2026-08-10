using System.Diagnostics;
using Respire.Commands;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// Distributed lock commands. Prefer <see cref="AcquireAsync(RespireKey, TimeSpan, CancellationToken)"/>
/// for managed locks. Use the token-based methods only when ownership must cross process boundaries.
/// </summary>
public interface ILockCommands
{
    /// <summary>
    /// Acquires a lock with a generated owner token. The returned attempt makes contention
    /// explicit through <see cref="RespireLockAttempt.Acquired"/>. Redis: SET ... NX PX.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var attempt = await redis.Locks.AcquireAsync("job:42", TimeSpan.FromSeconds(30));
    /// if (!attempt.Acquired) return; // someone else holds it
    /// var mutex = attempt.Lock;
    /// </code>
    /// </example>
    ValueTask<RespireLockAttempt> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a lock as <see cref="AcquireAsync(RespireKey, TimeSpan, CancellationToken)"/> does,
    /// but retries every 50 milliseconds until <paramref name="wait"/> elapses. Returns an
    /// unsuccessful attempt when the lock was still held at the end of that budget.
    /// </summary>
    ValueTask<RespireLockAttempt> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        CancellationToken cancellationToken = default);

    /// <summary>Polls at <paramref name="retryEvery"/> until <paramref name="wait"/> elapses.</summary>
    ValueTask<RespireLockAttempt> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a lock or throws <see cref="RespireLockNotAcquiredException"/> when another owner
    /// holds it. Use when contention is exceptional. Redis: SET ... NX PX.
    /// </summary>
    ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls for a lock until <paramref name="wait"/> elapses, then throws
    /// <see cref="RespireLockNotAcquiredException"/> if it is still held.
    /// </summary>
    ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        CancellationToken cancellationToken = default);

    /// <summary>Polls at <paramref name="retryEvery"/> and throws when the wait budget elapses.</summary>
    ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a lock when it does not already exist. The token identifies the owner and is
    /// required for later release or extension. Redis: SET ... NX PX.
    /// </summary>
    ValueTask<bool> TryTakeAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the lock only when its value still matches <paramref name="token"/>.
    /// Redis: EVALSHA/EVAL compare-and-DEL.
    /// </summary>
    ValueTask<bool> ReleaseAsync(
        RespireKey key,
        RespireValue token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lock expiry only when its value still matches <paramref name="token"/>.
    /// Redis: EVALSHA/EVAL compare-and-PEXPIRE.
    /// </summary>
    ValueTask<bool> ExtendAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the lock's current owner token, or null when missing. Redis: GET.</summary>
    ValueTask<byte[]?> GetOwnerTokenAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="mutex"/> still owns its key according to Redis.</summary>
    ValueTask<bool> IsHeldByAsync(RespireLock mutex, CancellationToken cancellationToken = default);
}

internal interface IManagedLockCommands
{
    ValueTask<bool> ExtendManagedAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        Action? onOutcomeUncertain,
        CancellationToken cancellationToken);
}

internal sealed class LockCommands(RespireClient client) : ILockCommands, IManagedLockCommands
{
    private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(50);

    internal static readonly RespireScript ReleaseScript = RespireScript.Create("""
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """);

    internal static readonly RespireScript ExtendScript = RespireScript.Create("""
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """);

    public async ValueTask<RespireLockAttempt> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        var token = RespireLock.NewToken();
        var acquiredTimestamp = Stopwatch.GetTimestamp();
        var mutex = await TryTakeAsync(key, token, expiry, cancellationToken).ConfigureAwait(false)
            ? new RespireLock(this, key, token, expiry, acquiredTimestamp)
            : null;
        return new RespireLockAttempt(mutex);
    }

    public ValueTask<RespireLockAttempt> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
        => AcquireAsync(key, expiry, wait, DefaultRetryInterval, cancellationToken);

    public async ValueTask<RespireLockAttempt> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default)
    {
        if (wait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(wait), wait, "Lock wait must not be negative.");
        }

        if (retryEvery <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryEvery),
                retryEvery,
                "Lock retry interval must be greater than zero.");
        }

        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var acquired = await AcquireAsync(key, expiry, cancellationToken).ConfigureAwait(false);
            if (acquired.Acquired)
            {
                return acquired;
            }

            var remaining = wait - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                return default;
            }

            await Task.Delay(
                    remaining < retryEvery ? remaining : retryEvery,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
        => (await AcquireAsync(key, expiry, wait, DefaultRetryInterval, cancellationToken).ConfigureAwait(false)).Lock;

    public async ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
        => (await AcquireAsync(key, expiry, cancellationToken).ConfigureAwait(false)).Lock;

    public async ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default)
        => (await AcquireAsync(key, expiry, wait, retryEvery, cancellationToken).ConfigureAwait(false)).Lock;

    public ValueTask<bool> TryTakeAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        return client.OkOrNullAsync(
            "SET",
            new LockTakeCommand(client.Key(in key), token, milliseconds),
            cancellationToken);
    }

    public ValueTask<bool> ReleaseAsync(
        RespireKey key,
        RespireValue token,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        return ExecuteBooleanScriptAsync(ReleaseScript, key, [token], cancellationToken);
    }

    public ValueTask<bool> ExtendAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        return ExecuteBooleanScriptAsync(ExtendScript, key, [token, milliseconds], cancellationToken);
    }

    async ValueTask<bool> IManagedLockCommands.ExtendManagedAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        Action? onOutcomeUncertain,
        CancellationToken cancellationToken)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        if (!client.RequiresReliableCorrectionOrdering(cancellationToken))
        {
            return await ExecuteBooleanScriptAsync(
                    ExtendScript, key, [token, milliseconds], cancellationToken)
                .ConfigureAwait(false);
        }

        await client.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
        RespireClient.TrackedScriptExecution? execution = null;
        try
        {
            execution = await client.StartTrackedScriptExecutionAsync(
                    ExtendScript, [key], [token, milliseconds], cancellationToken)
                .ConfigureAwait(false);
            using var result = await execution.Response.ConfigureAwait(false);
            return result.AsInteger() >= 1;
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or RespireTimeoutException or RespireConnectionException)
        {
            onOutcomeUncertain?.Invoke();
            if (execution?.ConnectionIdentity.ServerClientId > 0)
            {
                await client.FenceCorrectionConnectionAsync(execution.ConnectionIdentity).ConfigureAwait(false);
            }

            throw;
        }
    }

    public ValueTask<byte[]?> GetOwnerTokenAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.BytesOrNullAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public async ValueTask<bool> IsHeldByAsync(
        RespireLock mutex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        return await mutex.IsHeldByOriginAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> ExecuteBooleanScriptAsync(
        RespireScript script,
        RespireKey key,
        RespireValue[] args,
        CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteScriptAsync(
                script,
                client.BuildScriptTail([key], args),
                cancellationToken)
            .ConfigureAwait(false);
        return result.AsInteger() >= 1;
    }

    private static void ValidateToken(RespireValue token)
    {
        if (token.IsNull || token.IsEmpty)
        {
            throw new ArgumentException("Lock token must not be null or empty.", nameof(token));
        }
    }

    private static long ValidateExpiry(TimeSpan expiry)
    {
        var milliseconds = (long)expiry.TotalMilliseconds;
        if (milliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiry),
                expiry,
                "Lock expiry must be at least 1 millisecond.");
        }

        return milliseconds;
    }
}

/// <summary>SET key token NX PX milliseconds.</summary>
internal readonly struct LockTakeCommand(RespireValue key, RespireValue token, long milliseconds) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(6);
        writer.WriteRaw(Verbs.Set.Bulk);
        key.WriteTo(ref writer);
        token.WriteTo(ref writer);
        writer.WriteBulkString("NX"u8);
        writer.WriteBulkString("PX"u8);
        writer.WriteBulkInteger(milliseconds);
    }
}
