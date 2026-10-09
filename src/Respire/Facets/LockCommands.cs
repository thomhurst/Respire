using System.Diagnostics;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// Distributed lock commands. Prefer <see cref="AcquireAsync(RespireKey, TimeSpan, CancellationToken)"/>
/// for managed locks. Use the token-based methods only when ownership must cross process boundaries.
/// </summary>
/// <remarks>
/// Positive fractional milliseconds are rounded up to Redis millisecond precision, capped at the largest
/// whole millisecond representable by <see cref="TimeSpan"/>. Values above that cap are rounded down to it.
/// </remarks>
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
    public static ValueTask<RespireLockAttempt> AcquireAsync(
        this ILockCommands locks,
        RespireKey key,
        TimeSpan expiry,
        bool keepAlive,
        CancellationToken cancellationToken = default)
        => locks is not null and not LockCommands
            ? AcquireWrappedAsync(locks, key, expiry, keepAlive, cancellationToken)
            : DispatchResponseSource<RespireLockAttempt>.Run(
                (Locks: locks, Key: key, Expiry: expiry, KeepAlive: keepAlive, Token: cancellationToken),
                static (state, owner) => AcquireBorrowedAsync(state.Locks!, state.Key, state.Expiry, state.KeepAlive, state.Token, owner));

    private static async ValueTask<RespireLockAttempt> AcquireWrappedAsync(
        ILockCommands locks, RespireKey key, TimeSpan expiry, bool keepAlive, CancellationToken cancellationToken)
    {
        // A public implementation may forward to a native caller boundary. Let acquisition
        // retain its own final publisher; only keep-alive startup belongs to this extension.
        var attempt = await locks.AcquireAsync(key, expiry, cancellationToken).ConfigureAwait(false);
        if (!keepAlive || !attempt.Acquired) return attempt;
        return await DispatchResponseSource<RespireLockAttempt>.Run(attempt,
            static (attempt, owner) => StartKeepAliveBorrowedAsync(attempt, owner)).ConfigureAwait(false);
    }

    private static async ValueTask<RespireLockAttempt> AcquireBorrowedAsync(
        ILockCommands locks, RespireKey key, TimeSpan expiry, bool keepAlive,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(locks);
        var attempt = await ((LockCommands)locks).AcquireBorrowedAsync(key, expiry, cancellationToken, observation)
            .ConfigureAwait(false);
        if (!keepAlive || !attempt.Acquired)
        {
            return attempt;
        }

        return await StartKeepAliveBorrowedAsync(attempt, observation).ConfigureAwait(false);
    }

    private static async ValueTask<RespireLockAttempt> StartKeepAliveBorrowedAsync(
        RespireLockAttempt attempt, RespireTelemetry.ErrorObservation observation)
    {
        try
        {
            attempt.Lock.StartOwnedKeepAlive();
            return attempt;
        }
        catch
        {
            await attempt.Lock.DisposeBorrowedAsync(observation).ConfigureAwait(false);
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

    public ValueTask<RespireLockAttempt> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
        => DispatchResponseSource<RespireLockAttempt>.Run((Locks: this, Key: key, Expiry: expiry, Token: cancellationToken),
            static (state, owner) => state.Locks.AcquireBorrowedAsync(state.Key, state.Expiry, state.Token, owner));

    internal async ValueTask<RespireLockAttempt> AcquireBorrowedAsync(
        RespireKey key, TimeSpan expiry, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        var normalizedExpiry = TimeSpan.FromTicks(ValidateExpiry(expiry) * TimeSpan.TicksPerMillisecond);
        var token = RespireLock.NewToken();
        var acquiredTimestamp = Stopwatch.GetTimestamp();
        var mutex = await TryTakeBorrowedAsync(key, token, normalizedExpiry, cancellationToken, observation).ConfigureAwait(false)
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

    public ValueTask<RespireLockAttempt> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default)
        => DispatchResponseSource<RespireLockAttempt>.Run(
            (Locks: this, Key: key, Expiry: expiry, Wait: wait, Retry: retryEvery, Token: cancellationToken),
            static (state, owner) => state.Locks.AcquireBorrowedAsync(state.Key, state.Expiry, state.Wait, state.Retry, state.Token, owner));

    private async ValueTask<RespireLockAttempt> AcquireBorrowedAsync(
        RespireKey key, TimeSpan expiry, TimeSpan wait, TimeSpan retryEvery,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
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
            var acquired = await AcquireBorrowedAsync(key, expiry, cancellationToken, observation).ConfigureAwait(false);
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

    public ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
        => AcquireOrThrowAsync(key, expiry, wait, DefaultRetryInterval, cancellationToken);

    public ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
        => DispatchResponseSource<RespireLock>.Run((Locks: this, Key: key, Expiry: expiry, Token: cancellationToken),
            static async (state, owner) => (await state.Locks.AcquireBorrowedAsync(
                state.Key, state.Expiry, state.Token, owner).ConfigureAwait(false)).Lock);

    public ValueTask<RespireLock> AcquireOrThrowAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default)
        => DispatchResponseSource<RespireLock>.Run(
            (Locks: this, Key: key, Expiry: expiry, Wait: wait, Retry: retryEvery, Token: cancellationToken),
            static async (state, owner) => (await state.Locks.AcquireBorrowedAsync(
                state.Key, state.Expiry, state.Wait, state.Retry, state.Token, owner).ConfigureAwait(false)).Lock);

    public ValueTask<bool> TryTakeAsync(
        RespireKey key,
        RespireLockToken token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
        => DispatchResponseSource<bool>.Run((Locks: this, Key: key, Token: token, Expiry: expiry, Cancellation: cancellationToken),
            static (state, owner) => state.Locks.TryTakeBorrowedAsync(state.Key, state.Token, state.Expiry, state.Cancellation, owner));

    private ValueTask<bool> TryTakeBorrowedAsync(
        RespireKey key, RespireLockToken token, TimeSpan expiry, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        return client.OkOrNullAsync(
            "SET",
            new LockTakeCommand(client.Key(in key), token.AsValue(), milliseconds),
            cancellationToken, observation);
    }

    public ValueTask<bool> ReleaseAsync(
        RespireKey key,
        RespireLockToken token,
        CancellationToken cancellationToken = default)
        => DispatchResponseSource<bool>.Run((Locks: this, Key: key, Token: token, Cancellation: cancellationToken),
            static (state, owner) => state.Locks.ReleaseBorrowedAsync(state.Key, state.Token, state.Cancellation, owner));

    private ValueTask<bool> ReleaseBorrowedAsync(
        RespireKey key, RespireLockToken token, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ValidateToken(token);
        return client.ExecuteLockAsync(key, token, null, cancellationToken, observation);
    }

    ValueTask<bool> IManagedLockCommands.ReleaseManagedAsync(
        RespireKey key,
        RespireLockToken token,
        Action onOutcomeUncertain,
        CancellationToken cancellationToken)
        => DispatchResponseSource<bool>.Run(
            (Locks: this, Key: key, Token: token, Uncertain: onOutcomeUncertain, Cancellation: cancellationToken),
            static (state, owner) => state.Locks.ReleaseManagedBorrowedAsync(state.Key, state.Token, state.Uncertain, state.Cancellation, owner));

    internal async ValueTask<bool> ReleaseManagedBorrowedAsync(
        RespireKey key, RespireLockToken token, Action onOutcomeUncertain,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ValidateToken(token);
        RespireClient.TrackedLockExecution execution;
        bool fenced;
        try
        {
            (execution, fenced) = await StartReleaseExecutionAsync(key, token, cancellationToken, observation)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new LockReleaseNotSubmittedException(error);
        }

        try
        {
            return await client.ExecuteWithCorrectionAsync(execution,
                fenced ? RespireClient.CorrectionOrdering.BestEffortLockFence : RespireClient.CorrectionOrdering.NotifyOnly,
                onOutcomeUncertain: onOutcomeUncertain).ConfigureAwait(false);
        }
        catch (Exception error) when (!execution.CommandMayBeOutstanding || RespireClient.IsCorrectionNotSubmitted(error))
        {
            // The router cleared this flag only on proof that no delete reached Redis.
            throw new LockReleaseNotSubmittedException(
                error is RespireCommandNotSubmittedException { InnerException: OperationCanceledException cause }
                    ? cause : error);
        }
    }

    /// <summary>
    /// Starts the compare-and-delete. Identity tracking and fencing are set up only when the
    /// release can become ambiguous (a cancellable token or a command timeout); otherwise the
    /// caller waits for the reply or connection loss and no CLIENT permissions are needed.
    /// Returns whether the execution is fenceable.
    /// </summary>
    private async ValueTask<(RespireClient.TrackedLockExecution Execution, bool Fenced)> StartReleaseExecutionAsync(
        RespireKey key, RespireLockToken token, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (client.RequiresReliableCorrectionOrdering(cancellationToken))
        {
            try
            {
                await client.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
                return (await client.StartLockExecutionAsync(
                        key, token, milliseconds: null, requireIdentity: true, allowUnfencedFallback: true,
                        cancellationToken, observation)
                    .ConfigureAwait(false), true);
            }
            catch (RespireServerException error) when (
                Infrastructure.RespireConnectionMultiplexer.IsDefinitiveCorrectionOrderingFailure(error))
            {
                // ACLs or servers that deny CLIENT ID or CLIENT KILL keep the compatible release.
                // Other server errors propagate. An uncertain outcome still fails closed, and a
                // latent compare-and-delete cannot match another owner's token. Operators are told
                // once that the fence is unavailable. A cluster redirect or replacement target
                // that denies them later gets the same fallback inside the routing loop.
                observation.Handled(error);
                client.LogUnfencedLockReleaseOnce(error);
            }
        }

        return (await client.StartLockExecutionAsync(
                key, token, milliseconds: null, requireIdentity: false, allowUnfencedFallback: false,
                cancellationToken, observation)
            .ConfigureAwait(false), false);
    }

    // Transport proof that an attempt was never enqueued: cancellation or a command timeout while
    // waiting for in-flight capacity, a connection retired before it accepted the command, or a
    // connection already closed when the command reached the write gate.
    internal static bool IsUnsubmitted(Exception error)
        => RespireClient.IsCorrectionNotSubmitted(error);

    public ValueTask<bool> ResetExpiryAsync(
        RespireKey key,
        RespireLockToken token,
        TimeSpan newDuration,
        CancellationToken cancellationToken = default)
        => DispatchResponseSource<bool>.Run((Locks: this, Key: key, Token: token, Duration: newDuration, Cancellation: cancellationToken),
            static (state, owner) => state.Locks.ResetExpiryBorrowedAsync(state.Key, state.Token, state.Duration, state.Cancellation, owner));

    private ValueTask<bool> ResetExpiryBorrowedAsync(
        RespireKey key, RespireLockToken token, TimeSpan newDuration, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(newDuration, nameof(newDuration));
        return client.ExecuteLockAsync(key, token, milliseconds, cancellationToken, observation);
    }

    ValueTask<bool> IManagedLockCommands.ExtendManagedAsync(
        RespireKey key,
        RespireLockToken token,
        TimeSpan expiry,
        Action? onOutcomeUncertain,
        CancellationToken cancellationToken)
        => DispatchResponseSource<bool>.Run(
            (Locks: this, Key: key, Token: token, Expiry: expiry, Uncertain: onOutcomeUncertain, Cancellation: cancellationToken),
            static (state, owner) => state.Locks.ExtendManagedBorrowedAsync(state.Key, state.Token, state.Expiry, state.Uncertain, state.Cancellation, owner));

    internal async ValueTask<bool> ExtendManagedBorrowedAsync(
        RespireKey key, RespireLockToken token, TimeSpan expiry, Action? onOutcomeUncertain,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        await client.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
        var execution = await client.StartLockExecutionAsync(
                key, token, milliseconds, requireIdentity: true, allowUnfencedFallback: false, cancellationToken, observation)
            .ConfigureAwait(false);
        return await client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.FenceFirst,
            onOutcomeUncertain: onOutcomeUncertain).ConfigureAwait(false);
    }

    // Reply payloads are pooled; the returned token must own its bytes.
    public ValueTask<RespireLockToken?> GetOwnerTokenAsync(RespireKey key, CancellationToken cancellationToken = default)
        => DispatchResponseSource<RespireLockToken?>.Run((Locks: this, Key: key, Token: cancellationToken),
            static (state, owner) => state.Locks.GetOwnerTokenBorrowedAsync(state.Key, state.Token, owner));

    internal ValueTask<RespireLockToken?> GetOwnerTokenBorrowedAsync(
        RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.ConvertResponseAsync<Cmd1, LockCommands, RespireLockToken?>(
            "GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken, this,
            static (LockCommands _, in RespValue value) => value.IsNull
                ? (RespireLockToken?)null
                : RespireLockToken.FromOwnedBytes(value.AsSpan().ToArray()), observation: observation);

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
        var milliseconds = RespireLock.NormalizeDuration(expiry).Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                expiry,
                "Lock expiry must be positive.");
        }

        return milliseconds;
    }
}

/// <summary>SET key token NX PX milliseconds.</summary>
internal readonly struct LockTakeCommand(RespireValue key, RespireValue token, long milliseconds) : IRespCommand
{
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(6);
        writer.WriteRaw(Verbs.Set.Bulk);
        key.WriteTo(ref writer);
        token.WriteTo(ref writer);
        writer.WriteRaw(CommandOptionFrames.NX);
        writer.WriteRaw(CommandOptionFrames.PX);
        writer.WriteBulkInteger(milliseconds);
    }
}

/// <summary>
/// Wraps a lock-release failure that provably happened before any delete was submitted.
/// <see cref="RespireLock"/> restores ownership on this signal and rethrows
/// <see cref="Exception.InnerException"/>; callers never see this type.
/// </summary>
internal sealed class LockReleaseNotSubmittedException(Exception error) : Exception(error.Message, error);
