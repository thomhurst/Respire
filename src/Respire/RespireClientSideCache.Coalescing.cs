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
    // Invalidation publishes its barrier before reading admissions, then joinable reads.
    // Admission reserves before the gate and releases only after publishing the joinable
    // count there. A publisher is observed by invalidation or sees the barrier and cannot join.
    private int _sharedReadInvalidations;
    private int _sharedReadAdmissions;
    private int _joinableSharedReadCount;

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

        var shared = JoinSharedRead<RespValue>(identity, out var owner);

        // Start outside the gate: transport callbacks and metrics may reenter the cache.
        // Every producer snapshots its inputs before its first asynchronous suspension.
        // ProduceSharedReadAsync must capture every failure in Completion; it is not awaited here.
        if (owner) _ = ProduceSharedReadAsync(shared, state, read);
        return WaitForSharedReadAsync(shared, cancellationToken);
    }

    internal ValueTask<GetReadResult> CoalesceGetReadAsync<TState>(
        ClientCacheCommandKey identity, TState state,
        Func<TState, CancellationToken, ValueTask<GetReadResult>> read,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.CoalesceConcurrentMisses) return read(state, cancellationToken);
        var shared = JoinSharedRead<GetReadResult>(identity, out var owner);
        if (owner) _ = ProduceSharedGetReadAsync(shared, state, read);
        return WaitForSharedGetReadAsync(shared, cancellationToken);
    }

    private SharedRead<T> JoinSharedRead<T>(ClientCacheCommandKey identity, out bool owner)
    {
        SharedRead shared;
        owner = false;
        // Reserve admission before taking the gate. An invalidation that sees no joinable
        // reads must still wait for a publisher already inside or approaching this gate.
        Interlocked.Increment(ref _sharedReadAdmissions);
        try
        {
            lock (_sharedReadLock)
            {
                ObjectDisposedException.ThrowIf(_sharedReadsStopped, this);
                if (Volatile.Read(ref _sharedReadInvalidations) != 0 || !_sharedReads.TryGetValue(identity, out shared!))
                {
                    // Borrowed binary arguments must not outlive the caller that supplied them.
                    shared = new SharedRead<T>(identity.Snapshot());
                    // Reads starting during invalidation cannot become joinable: their producer
                    // may still observe the store before its entries have been removed.
                    if (Volatile.Read(ref _sharedReadInvalidations) == 0)
                    {
                        _sharedReads.Add(shared.Identity, shared);
                        Volatile.Write(ref _joinableSharedReadCount, _sharedReads.Count);
                    }
                    _activeSharedReads.Add(shared);
                    owner = true;
                }
                shared.Waiters++;
            }
        }
        // Release only after publishing the joinable count under the gate. The invalidator
        // reads admissions before that count; reversing this order could hide a publisher.
        finally { Interlocked.Decrement(ref _sharedReadAdmissions); }

        return (SharedRead<T>)shared;
    }

    private async Task ProduceSharedReadAsync<TState>(
        SharedRead<RespValue> shared, TState state, Func<TState, CancellationToken, ValueTask<RespValue>> read)
    {
        RespValue owned = default;
        try
        {
            try
            {
                using var response = await read(state, shared.Cancellation.Token).ConfigureAwait(false);
                // ToOwned recursively allocates GC-owned arrays; it never rents buffers.
                // If every waiter cancels, Completion and this value become collectible.
                owned = response.ToOwned();
            }
            finally { FinishSharedRead(shared); }
            shared.Completion.TrySetResult(owned);
        }
        catch (Exception error)
        {
            owned.Dispose();
            // Producer cancellation can also come from client disposal or the command
            // deadline, independently of a waiter's token. Preserve that original failure.
            shared.Completion.TrySetException(error);
            // All callers may have canceled. Observe the producer failure even then.
            _ = shared.Completion.Task.Exception;
        }
    }

    private async ValueTask<RespValue> WaitForSharedReadAsync(
        SharedRead<RespValue> shared, CancellationToken cancellationToken)
    {
        try
        {
            var response = await shared.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // Completion removes the joinable identity before waking callers. Earlier
            // callers finish copying before decrementing Waiters, so the last caller can
            // take the producer's owned value without sharing mutable arrays.
            // Concurrent copiers can both miss the handoff. The unused original is GC-owned;
            // decrementing before copying would let another caller mutate its source bytes.
            lock (_sharedReadLock)
            {
                if (shared.Waiters == 1) return response;
            }
            return response.ToOwned();
        }
        finally { ReleaseSharedRead(shared); }
    }

    private async Task ProduceSharedGetReadAsync<TState>(
        SharedRead<GetReadResult> shared, TState state,
        Func<TState, CancellationToken, ValueTask<GetReadResult>> read)
    {
        GetReadResult owned = default;
        try
        {
            try
            {
                var result = await read(state, shared.Cancellation.Token).ConfigureAwait(false);
                using var response = result.Response;
                owned = result.ToOwned(shareText: true);
            }
            finally { FinishSharedRead(shared); }
            shared.Completion.TrySetResult(owned);
        }
        catch (Exception error)
        {
            owned.Response.Dispose();
            shared.Fail(error);
        }
    }

    private async ValueTask<GetReadResult> WaitForSharedGetReadAsync(
        SharedRead<GetReadResult> shared, CancellationToken cancellationToken)
    {
        try
        {
            var result = await shared.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // As with ordinary replies, copy mutable response storage before releasing
            // the waiter. Only canonical immutable text and the publication handle are shared.
            lock (_sharedReadLock)
            {
                if (shared.Waiters == 1) return result;
            }
            return result.ToOwned();
        }
        finally { ReleaseSharedRead(shared); }
    }

    private void ReleaseSharedRead(SharedRead shared)
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
        {
            _sharedReads.Remove(shared.Identity);
            Volatile.Write(ref _joinableSharedReadCount, _sharedReads.Count);
        }
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
        catch (AggregateException error)
        {
            // Factory cancellation callbacks are user code. Their failures must not
            // interrupt caller cancellation or disposal of the remaining resources.
            shared.Fail(error);
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
    // End joining before any cache state changes. Overlapping invalidations keep joining
    // disabled until all changes finish; no shared gate is held while touching cache stores.
    private void BeginSharedReadInvalidation()
    {
        if (!_options.CoalesceConcurrentMisses) return;
        Interlocked.Increment(ref _sharedReadInvalidations);
        // Read admissions first: a completed publisher releases its reservation only after
        // publishing the joinable count. Later admissions observe the invalidation barrier.
        if (Volatile.Read(ref _sharedReadAdmissions) == 0 && Volatile.Read(ref _joinableSharedReadCount) == 0)
            return;
        lock (_sharedReadLock)
        {
            // Observable measurement keeps user meter callbacks outside cache gates.
            if (_sharedReads.Count != 0) Interlocked.Add(ref _sharedReadRetirements, _sharedReads.Count);
            _sharedReads.Clear();
            Volatile.Write(ref _joinableSharedReadCount, 0);
        }
    }

    private void EndSharedReadInvalidation()
    {
        if (!_options.CoalesceConcurrentMisses) return;
        Interlocked.Decrement(ref _sharedReadInvalidations);
    }

    internal void StopSharedReads()
    {
        if (!_options.CoalesceConcurrentMisses) return;
        SharedRead[] cancel;
        lock (_sharedReadLock)
        {
            _sharedReadsStopped = true;
            _sharedReads.Clear();
            Volatile.Write(ref _joinableSharedReadCount, 0);
            cancel = _activeSharedReads.Where(BeginCancellation).ToArray();
        }
        foreach (var shared in cancel) CancelSharedRead(shared);
    }

    private abstract class SharedRead(ClientCacheCommandKey identity)
    {
        internal readonly ClientCacheCommandKey Identity = identity;
        internal readonly CancellationTokenSource Cancellation = new();
        internal int Waiters;
        internal bool Finished;
        internal bool CancellationStarted;
        internal bool Canceling;
        internal abstract void Fail(Exception error);
    }

    private sealed class SharedRead<T>(ClientCacheCommandKey identity) : SharedRead(identity)
    {
        internal readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override void Fail(Exception error)
        {
            Completion.TrySetException(error);
            // Every caller may have canceled before the producer fails.
            _ = Completion.Task.Exception;
        }
    }
}
