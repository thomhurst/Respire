using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private ValueTask<RespValue> CachedHashGetManyAsync(
        ClientSideCacheCoordinator cache, ClientSideCacheCoordinator.QueryRequest request,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fieldCount = request.Query.ArgumentCount - 1;
        var result = new RespValue[fieldCount];
        RespireValue[]? missingFields = null;
        int[]? missingIndexes = null;
        var missingCount = 0;
        var cachedCount = 0;
        var key = request.PrimaryKey;
        var generation = _core.Sentinel?.Current;
        for (var index = 0; index < fieldCount; index++)
        {
            var field = request.Query.GetArgument(index + 1);
            var fieldRequest = new ClientSideCacheCoordinator.QueryRequest(
                new ClientCacheCommandKey("HGET", key.AsValue(), field), key);
            if (cache.TryGet(in fieldRequest, out var cached))
            {
                cachedCount++;
                result[index] = cached.ToOwned();
            }
            else
            {
                // Snapshot before the first await: raw callers may own mutable binary fields.
                (missingFields ??= new RespireValue[fieldCount])[missingCount] = field.Snapshot();
                (missingIndexes ??= new int[fieldCount])[missingCount++] = index;
            }
        }
        // A lookup callback can retire the Sentinel generation between fields. Discard
        // every cached value and rebuild the full physical-key request before dispatch.
        if (!IsCacheGenerationCurrent(generation))
        {
            missingFields ??= new RespireValue[fieldCount];
            missingIndexes ??= new int[fieldCount];
            missingCount = fieldCount;
            for (var index = 0; index < fieldCount; index++)
            {
                result[index].Dispose();
                result[index] = default;
                missingFields[index] = request.Query.GetArgument(index + 1).Snapshot();
                missingIndexes[index] = index;
            }
            cachedCount = 0;
            generation = _core.Sentinel?.Current;
        }
        if (missingFields is null) return ValueTask.FromResult(RespValue.Array(result));
        if (missingCount != missingFields.Length) Array.Resize(ref missingFields, missingCount);
        RespireValue[]? allFields = null;
        if (cachedCount != 0)
        {
            allFields = new RespireValue[fieldCount];
            for (var index = 0; index < fieldCount; index++)
                allFields[index] = request.Query.GetArgument(index + 1).Snapshot();
        }
        return FetchHashFieldsAndCacheAsync(key.Snapshot(), missingFields, missingIndexes!, result,
            cache, cancellationToken, generation, allFields, observation);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> FetchHashFieldsAndCacheAsync(
        RespireKey key, RespireValue[] fields, int[] missingIndexes, RespValue[] result,
        ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        SentinelRouter.Generation? generation, RespireValue[]? allFields, RespireTelemetry.ErrorObservation observation = default)
    {
        // Share only the missing wire fields. Each waiter keeps its own cached values,
        // result ordering, and ownership, even when different full requests join this producer.
        using var response = await (cache.CoalesceConcurrentMisses
            ? cache.CoalesceReadAsync(new ClientCacheCommandKey("HMGET", key.AsValue(), fields),
                (Client: this, Key: key, Fields: fields, Cache: cache),
                static (state, token, producerObservation) => state.Client.FetchHashFieldsAsync(state.Key, state.Fields, state.Cache, token, producerObservation),
                cancellationToken, observation)
            : FetchHashFieldsAsync(key, fields, cache, cancellationToken, observation)).ConfigureAwait(false);
        for (var index = 0; index < fields.Length; index++)
            result[missingIndexes[index]] = response.AsArray()[index].ToOwned();
        var combined = RespValue.Array(result);
        if (allFields is null || IsCacheGenerationCurrent(generation)) return combined;
        combined.Dispose();
        return await FetchHashFieldsForCurrentGenerationAsync(key, allFields, cache, cancellationToken, observation).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> FetchHashFieldsForCurrentGenerationAsync(
        RespireKey key, RespireValue[] fields, ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
    {
        while (true)
        {
            var generation = _core.Sentinel?.Current;
            var response = await FetchHashFieldsAsync(key, fields, cache, cancellationToken, observation).ConfigureAwait(false);
            if (IsCacheGenerationCurrent(generation)) return response;
            response.Dispose();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> FetchHashFieldsAsync(
        RespireKey key, RespireValue[] fields, ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
    {
        // Each field uses the existing HGET identity and hash-key dependency. A single hash
        // invalidation therefore removes every projection, regardless of the requested list.
        // Each pending field owns a dependency lease. Always abandon remaining leases,
        // including partial registration, send failures, cancellation, and malformed replies.
        using var queryReads = new HashQueryReadScope(cache, key, fields);
        var tokens = queryReads.Tokens;
        var response = default(RespValue);
        try
        {
            Action? onRedirect = null;
            if (_core.Cluster is not null)
            {
                onRedirect = () =>
                {
                    for (var index = 0; index < tokens.Length; index++)
                        tokens[index] = cache.RebaseRead(in tokens[index]);
                };
            }
            response = await SendTrackedAsync("HMGET", new Cmd1N(Verbs.HMGet, key.AsValue(), fields),
                cancellationToken, onRedirect, observation: observation).ConfigureAwait(false);
            if (response.Type != RespDataType.Array || response.AsArray().Length != fields.Length)
                throw new RespireProtocolException($"HMGET must return an array with {fields.Length} field values.");
            // Validate the entire reply before publishing any field from an untrusted frame.
            for (var index = 0; index < fields.Length; index++)
            {
                var value = response.AsArray()[index];
                if (!value.IsNull && value.Type != RespDataType.BulkString)
                    throw new RespireProtocolException("HMGET field values must be bulk strings or null.");
            }
            for (var index = 0; index < fields.Length; index++)
            {
                var value = response.AsArray()[index];
                cache.CompleteRead(in tokens[index], in value, allowInsert: true);
            }
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // A value scope adds no separate owner allocation. Its array is also the
    // redirect callback's array, so disposal abandons the rebased leases.
    private readonly struct HashQueryReadScope : IDisposable
    {
        private readonly ClientSideCacheCoordinator _cache;
        internal ClientSideCacheCoordinator.QueryReadToken[] Tokens { get; }

        internal HashQueryReadScope(ClientSideCacheCoordinator cache, RespireKey key, RespireValue[] fields)
        {
            _cache = cache;
            Tokens = new ClientSideCacheCoordinator.QueryReadToken[fields.Length];
            var registered = 0;
            try
            {
                for (; registered < fields.Length; registered++)
                {
                    var request = new ClientSideCacheCoordinator.QueryRequest(
                        new ClientCacheCommandKey("HGET", key.AsValue(), fields[registered]), key);
                    Tokens[registered] = cache.BeginRead("HGET", in request);
                }
            }
            catch { Abandon(registered); throw; }
        }

        public void Dispose() => Abandon(Tokens.Length);

        private void Abandon(int registered)
        {
            var unused = default(RespValue);
            for (var index = 0; index < registered; index++)
                _cache.CompleteRead(in Tokens[index], in unused, allowInsert: false);
        }
    }
}
