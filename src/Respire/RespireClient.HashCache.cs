using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private ValueTask<RespValue> CachedHashGetManyAsync(
        ClientSideCacheCoordinator cache, ClientSideCacheCoordinator.QueryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fieldCount = request.Query.ArgumentCount - 1;
        var result = new RespValue[fieldCount];
        RespireValue[]? missingFields = null;
        int[]? missingIndexes = null;
        var missingCount = 0;
        var key = request.PrimaryKey;
        for (var index = 0; index < fieldCount; index++)
        {
            var field = request.Query.GetArgument(index + 1);
            var fieldRequest = new ClientSideCacheCoordinator.QueryRequest(
                new ClientCacheCommandKey("HGET", key.AsValue(), field), key);
            if (cache.TryGet(in fieldRequest, out var cached))
            {
                result[index] = cached.ToOwned();
            }
            else
            {
                // Snapshot before the first await: raw callers may own mutable binary fields.
                (missingFields ??= new RespireValue[fieldCount])[missingCount] = field.Snapshot();
                (missingIndexes ??= new int[fieldCount])[missingCount++] = index;
            }
        }
        if (missingFields is null) return ValueTask.FromResult(RespValue.Array(result));
        if (missingCount != missingFields.Length) Array.Resize(ref missingFields, missingCount);
        return FetchHashFieldsAndCacheAsync(key.Snapshot(), missingFields, missingIndexes!, result,
            cache, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> FetchHashFieldsAndCacheAsync(
        RespireKey key, RespireValue[] fields, int[] missingIndexes, RespValue[] result,
        ClientSideCacheCoordinator cache, CancellationToken cancellationToken)
    {
        // Share only the missing wire fields. Each waiter keeps its own cached values,
        // result ordering, and ownership, even when different full requests join this producer.
        using var response = await (cache.CoalesceConcurrentMisses
            ? cache.CoalesceReadAsync(new ClientCacheCommandKey("HMGET", key.AsValue(), fields),
                (Client: this, Key: key, Fields: fields, Cache: cache),
                static (state, token) => state.Client.FetchHashFieldsAsync(state.Key, state.Fields, state.Cache, token),
                cancellationToken)
            : FetchHashFieldsAsync(key, fields, cache, cancellationToken)).ConfigureAwait(false);
        for (var index = 0; index < fields.Length; index++)
            result[missingIndexes[index]] = response.AsArray()[index].ToOwned();
        return RespValue.Array(result);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> FetchHashFieldsAsync(
        RespireKey key, RespireValue[] fields, ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken)
    {
        // Each field uses the existing HGET identity and hash-key dependency. A single hash
        // invalidation therefore removes every projection, regardless of the requested list.
        // QueryReadToken is an owned snapshot of keys, epochs and the store. BeginRead
        // registers no pending reader state, so failed/cancelled reads need no abandonment.
        var tokens = new ClientSideCacheCoordinator.QueryReadToken[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            var request = new ClientSideCacheCoordinator.QueryRequest(
                new ClientCacheCommandKey("HGET", key.AsValue(), fields[index]), key);
            tokens[index] = cache.BeginRead("HGET", in request);
        }
        Action? onRedirect = null;
        if (_core.Cluster is not null)
        {
            onRedirect = () =>
            {
                for (var index = 0; index < tokens.Length; index++)
                    tokens[index] = cache.RebaseRead(in tokens[index]);
            };
        }
        var response = await SendTrackedAsync("HMGET", new Cmd1N(Verbs.HMGet, key.AsValue(), fields),
            cancellationToken, onRedirect).ConfigureAwait(false);
        try
        {
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
}
