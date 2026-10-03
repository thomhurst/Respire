using System.Diagnostics;

namespace Respire.Internal;

/// <summary>Owns correction ordering and bounded cleanup policy for one client core.</summary>
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
        TimeSpan attemptTimeout, CleanupRetryPolicy retry, Action<string, string> onAbandoned)
        => EnqueueAsync(async cancellationToken =>
        {
            var ordered = await fence.TryAsync(attemptTimeout, cancellationToken).ConfigureAwait(false);
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

    internal ValueTask<CleanupAttemptResult> TryAsync(TimeSpan timeout, CancellationToken stopping = default)
        => IsAcknowledged ? new(CleanupAttemptResult.Succeeded)
            : CorrectionCoordinator.AttemptAsync(owner, EnsureAsync, timeout, stopping, () => IsAcknowledged);
}

internal readonly record struct CleanupRetryPolicy(TimeSpan Limit, TimeSpan InitialDelay, TimeSpan MaximumDelay);
