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
        List<RespireValue>? missingFields = null;
        List<int>? missingIndexes = null;
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
                (missingFields ??= new(fieldCount)).Add(field.Snapshot());
                (missingIndexes ??= new(fieldCount)).Add(index);
            }
        }
        return missingFields is null
            ? ValueTask.FromResult(RespValue.Array(result))
            : FetchHashFieldsAndCacheAsync(key.Snapshot(), missingFields.ToArray(), missingIndexes!, result,
                cache, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> FetchHashFieldsAndCacheAsync(
        RespireKey key, RespireValue[] fields, List<int> missingIndexes, RespValue[] result,
        ClientSideCacheCoordinator cache, CancellationToken cancellationToken)
    {
        // Each field uses the existing HGET identity and hash-key dependency. A single hash
        // invalidation therefore removes every projection, regardless of the requested list.
        var tokens = new ClientSideCacheCoordinator.QueryReadToken[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            var request = new ClientSideCacheCoordinator.QueryRequest(
                new ClientCacheCommandKey("HGET", key.AsValue(), fields[index]), key);
            tokens[index] = cache.BeginRead("HGET", in request);
        }
        var allowInsert = true;
        Action<bool>? onRedirect = null;
        if (_core.Cluster is not null)
        {
            onRedirect = cacheable =>
            {
                for (var index = 0; index < tokens.Length; index++)
                    tokens[index] = cache.RebaseRead(in tokens[index]);
                allowInsert = cacheable;
            };
        }
        using var response = await SendTrackedAsync("HMGET", new Cmd1N(Verbs.HMGet, key.AsValue(), fields),
            cancellationToken, onRedirect).ConfigureAwait(false);
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
            cache.CompleteRead(in tokens[index], in value, allowInsert);
            result[missingIndexes[index]] = value.ToOwned();
        }
        return RespValue.Array(result);
    }
}
