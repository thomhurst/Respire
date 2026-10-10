using System.Diagnostics;

namespace Respire.Internal;

/// <summary>Owns correction ordering and bounded cleanup policy for one client core.</summary>
/// <remarks>
/// <para>Supplies fence state, bounded foreground observation, attempt classification, and background
/// queue admission for <see cref="RespireClient.ExecuteWithCorrectionAsync{TResult, TState}"/>.
/// Rejected or unanswered fences never dispatch dependent corrections. A successful transport drain
/// also establishes ordering without killing a possibly reused server client ID. Every bounded-attempt
/// call passes its core explicitly (<c>null</c> for an untracked client) so disposal classification
/// cannot be skipped through a default argument.</para>
/// <para><b>Strict</b> corrections and native extensions propagate fence failures; compatible managed
/// release logs them while keeping its original error and ownership-loss notice.</para>
/// <para><b>Semaphore</b> cleanup: one-second attempts and foreground waits, a one-minute retry window,
/// jittered exponential delays from 100 ms to 5 s. Confirmed core disposal or cleanup shutdown is
/// terminal; another resource's disposal stays retryable while the owning core is live. Without a
/// core, <see cref="ObjectDisposedException"/> stays terminal for untracked <see cref="IRespireClient"/>
/// implementations. Server errors and attempt timeouts are retryable. The core queue keeps four
/// workers, 256 queued items, and at most 256 admission waiters, with overload and abandonment
/// diagnostics.</para>
/// <para><b>Cache</b> corrections first use owner-checked FIFO broadcasts; a completed broadcast proves
/// ordering without a kill or fence. Only an overdue pass creates a fence for the captured connection,
/// keeping its original peer and ASK state until the broadcast completes. That control connection uses
/// the independent <c>ConnectTimeout</c> (10 s default), because a cold handshake can outlast the
/// broadcast wait and fence sends disable command deadlines. Timeout propagates without ordering proof
/// or a dependent retry; cancellation retires the control connection. Each pass gets fresh TTL
/// arguments; passes stop when latency no longer halves or falls below tolerance. A detached
/// shrink-only pass stays observed and is safe if it lands. Every convergence call is awaited (the
/// <c>ExecuteWithCorrectionAsync</c> callback, <c>CapDelayedTtlAsync</c>, <c>CapRefreshedTtlAsync</c>),
/// so fence failures propagate.</para>
/// <para><b>Hash-field lease</b> cleanup keeps owner-checked scripts and Sentinel/Cluster route
/// targeting. Shared foreground observation and capped probe delays bound the caller's wait without
/// cancelling owed FIFO corrections; later failures are observed
/// (<c>BestEffortReleaseHashFieldLeaseAsync</c> catches completed failures, <see cref="WaitAsync(Task, CancellationToken)"/>
/// attaches a fault observer after a foreground deadline, <c>CorrectHashFieldLeaseAsync</c> observes
/// unfinished original and routed release tasks).</para>
/// <para><b>Untracked</b> <see cref="IRespireClient"/> implementations get best-effort cleanup with the
/// same retry mechanics, but no core-owned queue or explicit fence acknowledgement.</para>
/// </remarks>
internal sealed class CorrectionCoordinator(ClientCore core)
{
    internal CorrectionFence CreateFence(RespireClient client, RespireClient.TrackedConnectionIdentity identity)
        => new(identity, client.SendCorrectionFenceAsync, core);

    internal Task<bool> EnqueueAsync(
        Func<CancellationToken, ValueTask<CleanupAttemptResult>> attempt, Func<bool>? shouldContinue,
        CleanupRetryPolicy policy, Action<string> onAbandoned)
    {
        var queue = core.CoordinationCleanupQueue;
        if (queue is not null)
            return queue.EnqueueAsync(attempt, shouldContinue, policy.Limit, policy.InitialDelay,
                policy.MaximumDelay, onAbandoned);
        try { onAbandoned("client_disposed"); }
        catch { /* Diagnostics must not stop cleanup callers. */ }
        return Task.FromResult(false);
    }

    internal Task<bool> EnqueueFencedAsync(
        CorrectionFence fence, Func<CancellationToken, ValueTask> correct,
        TimeSpan attemptTimeout, CleanupRetryPolicy retry, Action<string, string> onAbandoned,
        Action<Exception>? onFenceFailure = null)
        => EnqueueAsync(async cancellationToken =>
        {
            var ordered = await fence.TryAsync(attemptTimeout, cancellationToken, onFenceFailure).ConfigureAwait(false);
            if (ordered != CleanupAttemptResult.Succeeded) return ordered;
            return await AttemptAsync(core, correct, attemptTimeout, cancellationToken).ConfigureAwait(false);
        }, null, retry, reason => onAbandoned(fence.IsAcknowledged ? "release" : "fence", reason));

    /// <summary>Classifies one bounded attempt. Only acknowledged ordering survives a later local failure.</summary>
    internal static ValueTask<CleanupAttemptResult> AttemptAsync(
        ClientCore? owner, Func<CancellationToken, ValueTask> attempt, TimeSpan timeout,
        CancellationToken stopping = default, Func<bool>? acknowledged = null)
        => AttemptAsync(owner, attempt, static (send, token) => send(token), timeout, stopping, acknowledged);

    internal static async ValueTask<CleanupAttemptResult> AttemptAsync<TState>(
        ClientCore? owner, TState state, Func<TState, CancellationToken, ValueTask> attempt,
        TimeSpan timeout, CancellationToken stopping = default, Func<bool>? acknowledged = null)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        bound.CancelAfter(timeout);
        try
        {
            await attempt(state, bound.Token).ConfigureAwait(false);
            return CleanupAttemptResult.Succeeded;
        }
        catch (Exception error)
        {
            if (acknowledged?.Invoke() == true) return CleanupAttemptResult.Succeeded;
            // Legacy clients have no core to inspect, so preserve their terminal disposal contract.
            return error is ObjectDisposedException && (owner is null || owner.Disposed)
                || error is OperationCanceledException && stopping.IsCancellationRequested
                ? CleanupAttemptResult.Abandoned : CleanupAttemptResult.Failed;
        }
    }

    /// <summary>Bounds foreground observation without cancelling an already owed ordered correction.</summary>
    internal static async ValueTask<bool> WaitAsync(Task correction, TimeSpan timeout)
    {
        try
        {
            await correction.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            Observe(correction);
            return false;
        }
    }

    internal static async ValueTask<bool> WaitAsync(Task correction, CancellationToken foregroundDeadline)
    {
        try
        {
            await correction.WaitAsync(foregroundDeadline).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (foregroundDeadline.IsCancellationRequested)
        {
            Observe(correction);
            return false;
        }
    }

    internal static void Observe(Task correction)
        => _ = correction.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Runs FIFO-ordered, shrink-only corrections until latency stops improving. The caller
    /// calculates fresh TTL arguments for each send; a timeout never fabricates ordering proof.
    /// </summary>
    internal static ValueTask ConvergeAsync(
        RespireClient.TrackedConnectionIdentity identity, CorrectionFence? fence,
        Func<bool, RespireClient.TrackedConnectionIdentity, Task> send,
        TimeSpan waitBound, TimeSpan tolerance)
        => ConvergeAsync(identity, (Fence: fence, Send: send), static (state, _) => state.Fence,
            static (state, ordered, original) => state.Send(ordered, original), waitBound, tolerance);

    internal static async ValueTask ConvergeAsync<TState>(
        RespireClient.TrackedConnectionIdentity identity, TState state,
        Func<TState, RespireClient.TrackedConnectionIdentity, CorrectionFence?> createFence,
        Func<TState, bool, RespireClient.TrackedConnectionIdentity, Task> send,
        TimeSpan waitBound, TimeSpan tolerance)
    {
        var previous = TimeSpan.MaxValue;
        var requiresOrdering = identity.ServerClientId > 0;
        var canFence = requiresOrdering;
        while (true)
        {
            var sent = Stopwatch.GetTimestamp();
            var pass = send(state, requiresOrdering, identity);
            if (!await WaitAsync(pass, waitBound).ConfigureAwait(false))
            {
                if (!canFence) return; // The idempotent ordered pass can still complete later.
                var fence = createFence(state, identity);
                if (fence is null) return;
                // Rejected or unanswered fences propagate; no dependent pass may run without proof.
                await fence.EnsureBoundedAsync().ConfigureAwait(false);
                canFence = false;
                // Preserve the original peer and FIFO/ASK route until a broadcast completes.
                identity = identity with { ServerClientId = 0 };
                previous = TimeSpan.MaxValue;
                continue;
            }
            requiresOrdering = false;
            identity = default; // A completed broadcast is FIFO proof, so subsequent passes need no kill.
            canFence = false;
            var roundTrip = Stopwatch.GetElapsedTime(sent);
            if (roundTrip < tolerance || roundTrip > previous / 2) return;
            previous = roundTrip;
        }
    }
}

/// <summary>
/// One correction's immutable physical identity and monotonic fence acknowledgement.
/// The owner serializes attempts (the cleanup queue runs one attempt per item at a time).
/// A transport callback publishes acknowledgement with explicit cross-thread visibility.
/// </summary>
/// <remarks>The identity is captured once. A successful <c>CLIENT KILL</c> reply stays proof even if
/// local socket retirement later fails; queued release retries reuse it and never kill the same
/// identity again. Debug builds assert that sends never overlap.</remarks>
internal sealed class CorrectionFence(
    RespireClient.TrackedConnectionIdentity identity,
    Func<RespireClient.TrackedConnectionIdentity, CancellationToken, Action, ValueTask> send,
    ClientCore? owner = null)
{
    private bool _acknowledged;
    // The only accesses are arguments to Debug.Assert, so Release sends do no extra atomic work.
    private int _activeAttempts;
    internal bool IsAcknowledged => Volatile.Read(ref _acknowledged);

    internal async ValueTask EnsureBoundedAsync()
    {
        if (IsAcknowledged) return;
        // A cold control handshake can outlast the foreground broadcast wait. Fence sends
        // disable command deadlines, so give them the independent connection timeout budget.
        using var deadline = new CancellationTokenSource(owner?.Options.ConnectTimeout ?? TimeSpan.FromSeconds(10));
        await EnsureAsync(deadline.Token).ConfigureAwait(false);
    }

    internal async ValueTask EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (IsAcknowledged) return;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identity.ServerClientId);
        Debug.Assert(Interlocked.Increment(ref _activeAttempts) == 1, "The correction owner must serialize fence attempts.");
        try
        {
            await send(identity, cancellationToken, () => Volatile.Write(ref _acknowledged, true)).ConfigureAwait(false);
            // Successful transport drain also proves ordering without sending CLIENT KILL.
            Volatile.Write(ref _acknowledged, true);
        }
        finally
        {
            Debug.Assert(Interlocked.Decrement(ref _activeAttempts) == 0, "Fence attempts overlapped.");
        }
    }

    internal ValueTask<CleanupAttemptResult> TryAsync(TimeSpan timeout, CancellationToken stopping = default,
        Action<Exception>? onFailure = null)
    {
        if (IsAcknowledged) return new(CleanupAttemptResult.Succeeded);
        if (onFailure is null)
            return CorrectionCoordinator.AttemptAsync(owner, EnsureAsync, timeout, stopping, () => IsAcknowledged);
        return CorrectionCoordinator.AttemptAsync(owner, (Fence: this, OnFailure: onFailure),
                static async (state, token) =>
                {
                    try { await state.Fence.EnsureAsync(token).ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        state.OnFailure?.Invoke(error);
                        throw;
                    }
                }, timeout, stopping, () => IsAcknowledged);
    }
}

internal readonly record struct CleanupRetryPolicy(TimeSpan Limit, TimeSpan InitialDelay, TimeSpan MaximumDelay);
