using Reservoir;
using Respire.Internal;

namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    // Active calls, not store generations, own these entries. Clear/continuity swaps must
    // not make a still-running mutation disappear from publication or sharing checks.
    private readonly Dictionary<RespireKey, int> _mutationWriters = new();
    private const int MaxIdleMutationKeyCapacity = 4096;
    private int _activeMutations;
    private int _unknownMutations;

    private MutationFence BeginKeyMutation(RespireKey key)
        => BeginMutation(MutationFenceKind.Key, key, null);

    private MutationFence BeginKeysMutation(RespireKey[] keys)
        => BeginMutation(MutationFenceKind.Keys, default, keys);

    private MutationFence BeginMutation(MutationFenceKind kind, RespireKey key, RespireKey[]? keys)
    {
        MutationFence fence;
        lock (_queryLock)
        {
            var count = checked(_activeMutations + 1);
            var registered = 0;
            var unknownRegistered = false;
            try
            {
                if (kind == MutationFenceKind.All)
                {
                    _unknownMutations = checked(_unknownMutations + 1);
                    unknownRegistered = true;
                }
                else if (kind == MutationFenceKind.Key)
                {
                    AddMutationWriter(in key);
                    registered = 1;
                }
                else if (kind == MutationFenceKind.Keys)
                {
                    foreach (var affected in keys!)
                    {
                        AddMutationWriter(in affected);
                        registered++;
                    }
                }
                fence = MutationLease.Rent(this, kind, key, keys);
                Volatile.Write(ref _activeMutations, count);
            }
            catch
            {
                if (unknownRegistered) _unknownMutations--;
                else if (kind == MutationFenceKind.Key && registered != 0) RemoveMutationWriter(in key);
                else for (var index = 0; index < registered; index++) RemoveMutationWriter(in keys![index]);
                throw;
            }
        }
        try
        {
            ReinvalidateMutation(in fence);
            return fence;
        }
        catch
        {
            fence.AbortBeforeDispatch();
            throw;
        }
    }

    private void AddMutationWriter(in RespireKey key)
        => _mutationWriters[key] = _mutationWriters.TryGetValue(key, out var count) ? checked(count + 1) : 1;

    private void RemoveMutationWriter(in RespireKey key)
    {
        var count = _mutationWriters[key];
        if (count == 1) _mutationWriters.Remove(key);
        else _mutationWriters[key] = count - 1;
    }

    private bool HasActiveMutation(in RespireKey key)
    {
        if (Volatile.Read(ref _activeMutations) == 0) return false;
        lock (_queryLock) return _unknownMutations != 0 || _mutationWriters.ContainsKey(key);
    }

    // Caller already owns _queryLock. Read tokens remember suppression at admission,
    // so a reply from the write window cannot become cacheable after the writer leaves.
    private bool HasActiveMutation(RespireKey[] dependencies)
    {
        if (_activeMutations == 0) return false;
        if (_unknownMutations != 0) return true;
        foreach (var key in dependencies) if (_mutationWriters.ContainsKey(key)) return true;
        return false;
    }

    private bool HasActiveMutation(in ClientCacheCommandKey query)
    {
        if (Volatile.Read(ref _activeMutations) == 0) return false;
        lock (_queryLock)
        {
            if (_unknownMutations != 0) return true;
            var operation = query.Operation;
            var start = 0;
            var count = 1;
            if (operation == "LCS") count = 2;
            else if (UsesEveryArgumentAsKey(operation)) count = query.ArgumentCount;
            else if (operation == "JSON.MGET") count = query.ArgumentCount - 1;
            else if (UsesCountedKeys(operation))
            {
                start = 1;
                if (TryGetKeyCount(in query, out var keyCount)) count = keyCount;
            }
            for (var index = 0; index < count; index++)
                if (_mutationWriters.ContainsKey(query.GetArgument(start + index).AsKey())) return true;
            return false;
        }
    }

    private void EndMutation(in MutationFence fence, bool reinvalidate)
    {
        try
        {
            if (reinvalidate) ReinvalidateMutation(in fence, observe: false);
        }
        finally
        {
            lock (_queryLock)
            {
                if (fence.Kind == MutationFenceKind.All) _unknownMutations--;
                else if (fence.Kind == MutationFenceKind.Key)
                {
                    var key = fence.Key;
                    RemoveMutationWriter(in key);
                }
                else if (fence.Kind == MutationFenceKind.Keys)
                    foreach (var key in fence.Keys!) RemoveMutationWriter(in key);
                Volatile.Write(ref _activeMutations, _activeMutations - 1);
                // Large multi-key calls may grow the live map. Do not retain that peak
                // capacity once known-key writers retire, even during a blocking group wait.
                if (_mutationWriters.Count == 0 && _mutationWriters.EnsureCapacity(0) > MaxIdleMutationKeyCapacity)
                    _mutationWriters.TrimExcess();
            }
        }
    }

    // Keep completion observations for external invalidation subscribers, which do not
    // participate in our publication protocol. Meter callbacks stay on the logical caller,
    // where their exceptions have always surfaced, rather than a native retirement worker.
    private void ObserveMutationCompletion(in MutationFence fence)
    {
        if (fence.Kind == MutationFenceKind.Key)
        {
            var key = fence.Key;
            ObserveMutationCompletion(in key);
        }
        else if (fence.Kind == MutationFenceKind.Keys)
            foreach (var key in fence.Keys!) ObserveMutationCompletion(in key);
    }

    private void ObserveMutationCompletion(in RespireKey key)
    {
        Interlocked.Increment(ref _invalidations);
        PublishInvalidation(in key, RespireClientCacheInvalidationReason.LocalMutation);
        RespireTelemetry.ClientCacheInvalidations.Add(1);
    }

    internal sealed class MutationLease
    {
        private const long LogicalCompleted = 1;
        private const long Failed = 2;
        private const long NativeAttached = 4;
        private const long Reference = 8;
        // Keep 32 reference bits: a supported int-sized batch also retains its logical
        // owner. The remaining epoch bits never wrap; exhausted leases leave the pool.
        private const long EpochIncrement = 1L << 35;
        private const long EpochMask = ~(EpochIncrement - 1);
        private const long ReferenceMask = (EpochIncrement - 1) & ~7L;
        private static readonly ObjectPool<MutationLease, PoolPolicy> Pool = new(4096);
        private long _state;
        private ClientSideCacheCoordinator? _owner;
        private MutationFence _fence;

        internal static MutationFence Rent(ClientSideCacheCoordinator owner, MutationFenceKind kind,
            RespireKey key, RespireKey[]? keys)
        {
            var lease = Pool.Rent();
            var epoch = checked((lease._state & EpochMask) + EpochIncrement);
            lease._owner = owner;
            lease._fence = new MutationFence(kind, key, keys, lease, epoch);
            Volatile.Write(ref lease._state, epoch | Reference);
            return lease._fence;
        }

        internal bool RetainNative(long epoch)
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if ((state & EpochMask) != epoch || (state & ReferenceMask) == 0) return false;
                if ((state & ReferenceMask) == ReferenceMask)
                    throw new InvalidOperationException("Too many native owners for one cache mutation.");
                var next = (state + Reference) | NativeAttached;
                if (Interlocked.CompareExchange(ref _state, next, state) == state) return true;
            }
        }

        internal void Release(long epoch, bool logical, bool succeeded, ClientSideCacheCoordinator? owner = null)
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if ((state & EpochMask) != epoch || (state & ReferenceMask) == 0
                    || logical && (state & LogicalCompleted) != 0) return;
                if (owner is not null && !ReferenceEquals(owner, _owner))
                    return;
                var next = logical
                    ? state | LogicalCompleted | (succeeded ? 0 : Failed)
                    : state - Reference;
                if (Interlocked.CompareExchange(ref _state, next, state) != state) continue;
                if (logical)
                {
                    // Keep the logical reference while callbacks run: a simultaneous native
                    // release cannot recycle the owner or payload under this observation.
                    try { _owner!.ObserveMutationCompletion(in _fence); }
                    finally { Release(epoch, logical: false, succeeded: true); }
                    return;
                }
                if ((next & ReferenceMask) != 0) return;
                try
                {
                    var reinvalidate = _fence.Kind is MutationFenceKind.All or MutationFenceKind.FlushOnly
                        || (next & NativeAttached) == 0 || (next & Failed) != 0;
                    _owner!.EndMutation(in _fence, reinvalidate);
                }
                finally { Pool.Return(this); }
                return;
            }
        }

        private readonly struct PoolPolicy : IPooledObjectPolicy<MutationLease>
        {
            public MutationLease Create() => new();
            public bool TryReset(MutationLease lease)
            {
                lease._owner = null;
                lease._fence = default;
                // Never recycle the epoch: even an arbitrarily late copied fence must
                // not release a new rental. Exhausted objects are simply not retained.
                return (lease._state & EpochMask) <= long.MaxValue - EpochIncrement;
            }
        }
    }
}
