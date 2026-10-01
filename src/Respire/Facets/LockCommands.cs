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
        RespireLockToken token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the lock only when its value still matches <paramref name="token"/>.
    /// Uses native conditional deletion when supported, otherwise EVALSHA/EVAL compare-and-DEL.
    /// </summary>
    ValueTask<bool> ReleaseAsync(
        RespireKey key,
        RespireLockToken token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the lock expiry from now only when its value still matches <paramref name="token"/>.
    /// Uses SET IFEQ PX when supported, otherwise EVALSHA/EVAL compare-and-PEXPIRE.
    /// </summary>
    ValueTask<bool> ResetExpiryAsync(
        RespireKey key,
        RespireLockToken token,
        TimeSpan newDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the lock's current owner token, or null when missing. Redis: GET.</summary>
    ValueTask<RespireLockToken?> GetOwnerTokenAsync(RespireKey key, CancellationToken cancellationToken = default);
}

/// <summary>Convenience operations composed from managed distributed-lock commands.</summary>
public static class LockCommandExtensions
{
    /// <summary>
    /// Acquires a managed lock and, when requested, starts renewal owned by the returned lock
    /// handle. Disposing the attempt stops renewal and releases the lock.
    /// </summary>
    public static async ValueTask<RespireLockAttempt> AcquireAsync(
        this ILockCommands locks,
        RespireKey key,
        TimeSpan expiry,
        bool keepAlive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locks);
        var attempt = await locks.AcquireAsync(key, expiry, cancellationToken).ConfigureAwait(false);
        if (!keepAlive || !attempt.Acquired)
        {
            return attempt;
        }

        try
        {
            attempt.Lock.StartOwnedKeepAlive();
            return attempt;
        }
        catch
        {
            await attempt.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal interface IManagedLockCommands
{
    /// <summary>
    /// Releases a managed lock. Failures are classified explicitly so the handle never infers
    /// safety from a missing signal:
    /// <list type="bullet">
    /// <item><see cref="LockReleaseNotSubmittedException"/> wraps any failure that provably
    /// happened before a delete could reach Redis; ownership is unchanged and release may be
    /// retried.</item>
    /// <item><see cref="RespireServerException"/> is a definitive rejection of the delete.</item>
    /// <item>Anything else is uncertain. <paramref name="onOutcomeUncertain"/> runs before the
    /// connection is fenced, so protected work stops while the fence waits.</item>
    /// </list>
    /// </summary>
    ValueTask<bool> ReleaseManagedAsync(
        RespireKey key,
        RespireLockToken token,
        Action onOutcomeUncertain,
        CancellationToken cancellationToken);

    ValueTask<bool> ExtendManagedAsync(
        RespireKey key,
        RespireLockToken token,
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
        var normalizedExpiry = TimeSpan.FromMilliseconds(ValidateExpiry(expiry));
        var token = RespireLock.NewToken();
        var acquiredTimestamp = Stopwatch.GetTimestamp();
        var mutex = await TryTakeAsync(key, token, normalizedExpiry, cancellationToken).ConfigureAwait(false)
            ? new RespireLock(this, key, token, normalizedExpiry, acquiredTimestamp)
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
        RespireLockToken token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        return client.OkOrNullAsync(
            "SET",
            new LockTakeCommand(client.Key(in key), token.AsValue(), milliseconds),
            cancellationToken);
    }

    public ValueTask<bool> ReleaseAsync(
        RespireKey key,
        RespireLockToken token,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        return client.ExecuteLockAsync(key, token, null, cancellationToken);
    }

    async ValueTask<bool> IManagedLockCommands.ReleaseManagedAsync(
        RespireKey key,
        RespireLockToken token,
        Action onOutcomeUncertain,
        CancellationToken cancellationToken)
    {
        ValidateToken(token);
        RespireClient.TrackedLockExecution execution;
        bool fenced;
        try
        {
            // Identity setup and StartLockExecutionAsync finish before the routed send can fail:
            // send failures surface only through Response. Any error here, including cancellation
            // while CLIENT ID is set up or a connection is acquired, precedes submission.
            (execution, fenced) = await StartReleaseExecutionAsync(key, token, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new LockReleaseNotSubmittedException(error);
        }

        try
        {
            return await execution.Response.ConfigureAwait(false);
        }
        catch (Exception error) when (!execution.CommandMayBeOutstanding)
        {
            // The routing loop is the single source of truth for submission: it cleared the flag
            // only on proof that no delete reached Redis.
            throw new LockReleaseNotSubmittedException(
                error is RespireCommandNotSubmittedException { InnerException: OperationCanceledException cause }
                    ? cause
                    : error);
        }
        catch (RespireServerException)
        {
            throw;
        }
        catch (Exception error)
        {
            // The delete may have reached Redis. Report that before fencing so ownership loss is
            // visible while the fence waits for its control connection.
            onOutcomeUncertain();
            if (fenced && execution.ConnectionIdentity.ServerClientId > 0 && IsFenceableUncertainty(error))
            {
                // A fence failure is logged and must not replace the release error.
                await client.TryFenceLockConnectionAsync(execution.ConnectionIdentity, "lock release")
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Whether an uncertain release left its connection alive with the delete possibly still
    /// queued, so <c>CLIENT KILL</c> is needed to stop it. Connection failures and abandoned waits
    /// qualify. Other errors (a protocol fault, a disposed client) have already torn the
    /// connection down, so there is nothing left to fence; ownership is still treated as lost.
    /// </summary>
    private static bool IsFenceableUncertainty(Exception error)
        => error is OperationCanceledException or RespireTimeoutException or RespireConnectionException;

    /// <summary>
    /// Starts the compare-and-delete. Identity tracking and fencing are set up only when the
    /// release can become ambiguous (a cancellable token or a command timeout); otherwise the
    /// caller waits for the reply or connection loss and no CLIENT permissions are needed.
    /// Returns whether the execution is fenceable.
    /// </summary>
    private async ValueTask<(RespireClient.TrackedLockExecution Execution, bool Fenced)> StartReleaseExecutionAsync(
        RespireKey key, RespireLockToken token, CancellationToken cancellationToken)
    {
        if (client.RequiresReliableCorrectionOrdering(cancellationToken))
        {
            try
            {
                await client.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
                return (await client.StartLockExecutionAsync(
                        key, token, milliseconds: null, requireIdentity: true, cancellationToken)
                    .ConfigureAwait(false), true);
            }
            catch (RespireServerException error) when (
                Infrastructure.RespireConnectionMultiplexer.IsDefinitiveCorrectionOrderingFailure(error))
            {
                // ACLs or servers that deny CLIENT ID or CLIENT KILL keep the compatible release.
                // Other server errors propagate. An uncertain outcome still fails closed, and a
                // latent compare-and-delete cannot match another owner's token. Operators are told
                // once that the fence is unavailable.
                client.LogUnfencedLockReleaseOnce(error);
            }
        }

        return (await client.StartLockExecutionAsync(
                key, token, milliseconds: null, requireIdentity: false, cancellationToken)
            .ConfigureAwait(false), false);
    }

    // Transport proof that an attempt was never enqueued: cancellation or a command timeout while
    // waiting for in-flight capacity, or a connection retired before it accepted the command.
    internal static bool IsUnsubmitted(Exception error)
        => error is RespireCommandNotSubmittedException
            or Respire.Networking.RespireConnectionRetiredException
            or RespireTimeoutException { Diagnostics.Stage: RespireCommandStage.WaitingForCapacity };

    public ValueTask<bool> ResetExpiryAsync(
        RespireKey key,
        RespireLockToken token,
        TimeSpan newDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(newDuration, nameof(newDuration));
        return client.ExecuteLockAsync(key, token, milliseconds, cancellationToken);
    }

    async ValueTask<bool> IManagedLockCommands.ExtendManagedAsync(
        RespireKey key,
        RespireLockToken token,
        TimeSpan expiry,
        Action? onOutcomeUncertain,
        CancellationToken cancellationToken)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        await client.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
        RespireClient.TrackedLockExecution? execution = null;
        try
        {
            execution = await client.StartLockExecutionAsync(
                    key, token, milliseconds, requireIdentity: true, cancellationToken)
                .ConfigureAwait(false);
            return await execution.Response.ConfigureAwait(false);
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

    // Reply payloads are pooled; the returned token must own its bytes.
    public ValueTask<RespireLockToken?> GetOwnerTokenAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync<Cmd1, LockCommands, RespireLockToken?>(
            "GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken, this,
            static (LockCommands _, in RespValue value) => value.IsNull
                ? (RespireLockToken?)null
                : RespireLockToken.FromOwnedBytes(value.AsSpan().ToArray()));

    private static void ValidateToken(RespireLockToken token)
    {
        if (token.IsEmpty)
        {
            throw new ArgumentException("Lock token must not be null or empty.", nameof(token));
        }
    }

    private static long ValidateExpiry(TimeSpan expiry)
        => ValidateExpiry(expiry, nameof(expiry));

    private static long ValidateExpiry(TimeSpan expiry, string parameterName)
    {
        var milliseconds = (long)expiry.TotalMilliseconds;
        if (milliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
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

/// <summary>
/// Wraps a lock-release failure that provably happened before any delete was submitted.
/// <see cref="RespireLock"/> restores ownership on this signal and rethrows
/// <see cref="Exception.InnerException"/>; callers never see this type.
/// </summary>
internal sealed class LockReleaseNotSubmittedException(Exception error) : Exception(error.Message, error);
