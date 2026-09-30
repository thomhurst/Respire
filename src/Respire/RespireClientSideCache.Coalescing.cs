using Respire.Internal;
using Respire.Protocol;

namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    internal bool CoalesceConcurrentMisses => _options.CoalesceConcurrentMisses;

    private static long _sharedReadRetirements;
    internal static long SharedReadRetirements => Interlocked.Read(ref _sharedReadRetirements);

    private readonly Lock _sharedReadLock = new();
    private readonly Dictionary<ClientCacheCommandKey, SharedRead> _sharedReads = new();
    private readonly HashSet<SharedRead> _activeSharedReads = new();
    private bool _sharedReadsStopped;

    internal int ActiveSharedReadCount
    {
        get { lock (_sharedReadLock) return _activeSharedReads.Count; }
    }

    internal ValueTask<RespValue> CoalesceReadAsync<TState>(
        ClientCacheCommandKey identity, TState state,
        Func<TState, CancellationToken, ValueTask<RespValue>> read,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.CoalesceConcurrentMisses) return read(state, cancellationToken);

        SharedRead shared;
        var owner = false;
        lock (_sharedReadLock)
        {
            ObjectDisposedException.ThrowIf(_sharedReadsStopped, this);
            if (!_sharedReads.TryGetValue(identity, out shared!))
            {
                // Borrowed binary arguments must not outlive the caller that supplied them.
                shared = new SharedRead(identity.Snapshot());
                _sharedReads.Add(shared.Identity, shared);
                _activeSharedReads.Add(shared);
                owner = true;
            }
            shared.Waiters++;
        }

        // Start outside the gate: transport callbacks and metrics may reenter the cache.
        // Every producer snapshots its inputs before its first asynchronous suspension.
        // ProduceSharedReadAsync must capture every failure in Completion; it is not awaited here.
        if (owner) _ = ProduceSharedReadAsync(shared, state, read);
        return WaitForSharedReadAsync(shared, cancellationToken);
    }

    private async Task ProduceSharedReadAsync<TState>(
        SharedRead shared, TState state, Func<TState, CancellationToken, ValueTask<RespValue>> read)
    {
        try
        {
            using var response = await read(state, shared.Cancellation.Token).ConfigureAwait(false);
            var owned = response.ToOwned();
            FinishSharedRead(shared);
            shared.Completion.TrySetResult(owned);
        }
        catch (Exception error)
        {
            FinishSharedRead(shared);
            // Producer cancellation can also come from client disposal or the command
            // deadline, independently of a waiter's token. Preserve that original failure.
            shared.Completion.TrySetException(error);
            // All callers may have canceled. Observe the producer failure even then.
            _ = shared.Completion.Task.Exception;
        }
    }

    private async ValueTask<RespValue> WaitForSharedReadAsync(
        SharedRead shared, CancellationToken cancellationToken)
    {
        try
        {
            var response = await shared.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // Completion removes the joinable identity before waking callers. Earlier
            // callers finish copying before decrementing Waiters, so the last caller can
            // take the producer's owned value without sharing mutable arrays.
            lock (_sharedReadLock)
            {
                if (shared.Waiters == 1) return response;
            }
            return response.ToOwned();
        }
        finally
        {
            var cancel = false;
            var dispose = false;
            lock (_sharedReadLock)
            {
                if (--shared.Waiters == 0)
                {
                    RemoveSharedRead(shared);
                    cancel = BeginCancellation(shared);
                    dispose = shared.Finished && !shared.Canceling;
                }
            }
            if (cancel) CancelSharedRead(shared);
            else if (dispose) shared.Cancellation.Dispose();
        }
    }

    private void FinishSharedRead(SharedRead shared)
    {
        bool dispose;
        lock (_sharedReadLock)
        {
            shared.Finished = true;
            RemoveSharedRead(shared);
            _activeSharedReads.Remove(shared);
            dispose = shared.Waiters == 0 && !shared.Canceling;
        }
        if (dispose) shared.Cancellation.Dispose();
    }

    private void RemoveSharedRead(SharedRead shared)
    {
        if (_sharedReads.TryGetValue(shared.Identity, out var current) && ReferenceEquals(current, shared))
            _sharedReads.Remove(shared.Identity);
    }

    private static bool BeginCancellation(SharedRead shared)
    {
        if (shared.Finished || shared.CancellationStarted) return false;
        shared.CancellationStarted = shared.Canceling = true;
        return true;
    }

    private void CancelSharedRead(SharedRead shared)
    {
        try
        {
            shared.Cancellation.Cancel();
        }
        finally
        {
            bool dispose;
            lock (_sharedReadLock)
            {
                shared.Canceling = false;
                dispose = shared.Finished && shared.Waiters == 0;
            }
            if (dispose) shared.Cancellation.Dispose();
        }
    }

    // Called under membership/health gates too. Never cancel or invoke callbacks here.
    // Conservatively retire all identities: an invalidation of any dependency ends joining.
    private void RetireSharedReads()
    {
        if (!_options.CoalesceConcurrentMisses) return;
        lock (_sharedReadLock)
        {
            // Observable measurement keeps user meter callbacks outside cache gates.
            if (_sharedReads.Count != 0) Interlocked.Add(ref _sharedReadRetirements, _sharedReads.Count);
            _sharedReads.Clear();
        }
    }

    internal void StopSharedReads()
    {
        if (!_options.CoalesceConcurrentMisses) return;
        SharedRead[] cancel;
        lock (_sharedReadLock)
        {
            _sharedReadsStopped = true;
            _sharedReads.Clear();
            cancel = _activeSharedReads.Where(BeginCancellation).ToArray();
        }
        foreach (var shared in cancel) CancelSharedRead(shared);
    }

    private sealed class SharedRead(ClientCacheCommandKey identity)
    {
        internal readonly ClientCacheCommandKey Identity = identity;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<RespValue> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
        internal bool Finished;
        internal bool CancellationStarted;
        internal bool Canceling;
    }
}
