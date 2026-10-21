using Respire.Protocol;

namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    // Owned by a retained socket, under its observation gate. Keep the union of keys
    // for the socket's entire lifetime: eviction/expiry does not retire an in-flight fill.
    // Overflow drops the owned keys and restores the conservative whole-cache fence.
    internal sealed class TrackingProvenance
    {
        internal const int MaximumKeys = 4096;
        internal const int MaximumKeyBytes = 1024 * 1024;
        private HashSet<RespireKey>? _keys = new();
        private int _keyBytes;

        internal void Forget() => _keys = null;

        internal void Register<TCommand>(string operation, in TCommand command)
            where TCommand : struct, IRespCommand
        {
            if (_keys is null) return;
            Register(TryCreateQuery(operation, in command, out var request) && request.Query.Operation == operation ? request : null);
        }

        internal void Register(QueryRequest? read, ClientSideCacheCoordinator? cache = null)
        {
            if (_keys is null) return;
            if (read is not { } request)
            {
                _keys = null;
                return;
            }
            var query = request.Query;
            var operation = query.Operation;
            // Match CreateDependencies, including custom commands whose primary key
            // is not the first argument of their exact-query identity.
            var start = -1;
            var count = 1;
            if (operation == "LCS") { start = 0; count = 2; }
            else if (UsesEveryArgumentAsKey(operation)) { start = 0; count = query.ArgumentCount; }
            else if (operation == "JSON.MGET") { start = 0; count = query.ArgumentCount - 1; }
            else if (UsesCountedKeys(operation))
            {
                start = 1;
                TryGetKeyCount(in query, out count);
            }
            for (var index = 0; index < count; index++)
            {
                var key = start < 0 ? request.PrimaryKey : query.GetArgument(start + index).AsKey();
                // MGET can mix eligible and excluded tokens. Coverage is immutable and
                // matches BeginRead's per-key CanCache state; excluded keys never fill
                // the cache and must not exhaust this socket's provenance budget.
                if (cache is not null && !cache.CanTrack(in key)) continue;
                if (_keys.Contains(key)) continue;
                var length = key.WireLength;
                if (_keys.Count == MaximumKeys || length > MaximumKeyBytes - _keyBytes)
                {
                    _keys = null;
                    return;
                }
                _keys.Add(key.Snapshot());
                _keyBytes += length;
            }
        }

        internal int Fence(ClientSideCacheCoordinator cache)
        {
            if (_keys is null) return cache.FlushForContinuityLossWithoutMetrics();
            var removed = 0;
            foreach (var key in _keys)
            {
                // Reuse both GET and exact-query generation barriers. Shared keys are
                // deliberately evicted even when another healthy socket read them too.
                removed += cache.InvalidateState(in key);
                cache.PublishInvalidation(in key, RespireClientCacheInvalidationReason.ContinuityLost);
            }
            Forget();
            if (removed != 0) Interlocked.Add(ref cache._evictions, removed);
            Interlocked.Increment(ref cache._continuityFlushes);
            return removed;
        }
    }
}
