using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

public sealed partial class RespireClient
{
    private static long _nextCacheAsideTypeIdentity;

    /// <inheritdoc cref="IRespireClient.GetOrSetAsync{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetOrSetAsync<T>(RespireKey key, Func<CancellationToken, ValueTask<T?>> factory,
        RespireExpiry expiry, CancellationToken cancellationToken = default)
        => DispatchResponseSource<T?>.Run(
            (Client: this, Key: key, Factory: factory, Expiry: expiry, Token: cancellationToken),
            static (state, observation) => state.Client.GetOrSetCoreAsync(
                state.Key, state.Factory, state.Expiry, state.Token, observation));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T?> GetOrSetCoreAsync<T>(RespireKey key, Func<CancellationToken, ValueTask<T?>> factory,
        RespireExpiry expiry, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!(expiry.TryGetRelativeMilliseconds(out var milliseconds)
                || expiry.TryGetAbsoluteUnixMilliseconds(out milliseconds)) || milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(expiry),
                "Expiry must be a relative duration of at least one millisecond or an absolute instant after the Unix epoch.");
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_core.ClientCache is null)
            throw new InvalidOperationException("GetOrSetAsync requires ClientSideCache to be enabled.");
        var resolvedKey = ResolveKey(key);
        if (ReadCache is not { } cache)
            return GetOrSetUncachedAsync(resolvedKey.Snapshot(), factory, expiry, cancellationToken, observation);
        if (typeof(T) == typeof(string))
        {
            if (cache.TryGetString(in resolvedKey, out var cachedText) && cachedText is not null)
                return new ValueTask<T?>((T)(object)cachedText);
        }
        else if (cache.TryGet(in resolvedKey, out var cached) && !cached.IsNull)
            return new ValueTask<T?>(DeserializeBorrowed<T>(in cached));

        return GetOrSetMissAsync(resolvedKey.Snapshot(), factory, expiry, cache, cancellationToken, observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<T?> GetOrSetUncachedAsync<T>(RespireKey key, Func<CancellationToken, ValueTask<T?>> factory,
        RespireExpiry expiry, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        // WithoutClientCache: read Redis directly and never join or populate local cache work.
        using var response = await ProduceCacheAsideAsync(key, factory, expiry, cancellationToken,
            observation, useCache: false)
            .ConfigureAwait(false);
        return DeserializeBorrowed<T>(in response);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<T?> GetOrSetMissAsync<T>(RespireKey key, Func<CancellationToken, ValueTask<T?>> factory,
        RespireExpiry expiry, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        // A factory is not a GET producer. Types and expiry forms/values with different contracts never join.
        // Within one identity the first factory wins; callers must agree on its meaning.
        var absolute = expiry.TryGetAbsoluteUnixMilliseconds(out var milliseconds);
        if (!absolute) expiry.TryGetRelativeMilliseconds(out milliseconds);
        using var response = cache.CoalesceConcurrentMisses
            ? await cache.CoalesceReadAsync(
                new ClientCacheCommandKey("GETORSET", key.AsValue(), CacheAsideType<T>.Identity,
                    absolute ? "PXAT" : "PX", milliseconds),
                (Client: this, Key: key, Factory: factory, Expiry: expiry),
                static (state, token, producerObservation) => state.Client.ProduceCacheAsideAsync(
                    state.Key, state.Factory, state.Expiry, token, producerObservation),
                cancellationToken, observation).ConfigureAwait(false)
            : await ProduceCacheAsideAsync(key, factory, expiry, cancellationToken, observation).ConfigureAwait(false);
        return DeserializeBorrowed<T>(in response);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<RespValue> ProduceCacheAsideAsync<T>(RespireKey key,
        Func<CancellationToken, ValueTask<T?>> factory, RespireExpiry expiry, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation, bool useCache = true)
    {
        // Use normal tracked reads and their insertion fences. Never insert a SET reply into
        // the cache: the write can invalidate tracking, and another writer may already follow it.
        // The public entry already counted this caller's lookup. Recheck without counting
        // again because another producer may have filled the cache before this one starts.
        var cache = _core.ClientCache!;
        var existing = !useCache
            ? await SendCoreAsync("GET", new Cmd1(Verbs.Get, key.AsValue()), cancellationToken,
                RespireCommandFlags.None, allowReadFrom: true, cursorAffinity: null,
                observation: observation, observeErrors: false).ConfigureAwait(false)
            : cache.TryPeek(in key, out var cached)
                ? cached.ToOwned()
                : await GetAndCacheAsync(key, cache, cancellationToken,
                    static (RespireClient _, in RespValue value) => value.ToOwned(), observation).ConfigureAwait(false);
        if (!existing.IsNull) return existing;
        existing.Dispose();

        var created = await factory(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        if (created is null) return RespValue.Null;

        var serialized = Serialize(created);
        RespireValue.ThrowIfNull(serialized, nameof(factory));
        var bytes = new byte[serialized.GetWireLength()];
        serialized.WriteWirePayload(bytes);
        var command = new SetCommand(key.AsValue(), bytes, expiry, RespireSetWhen.NotExists, returnOld: true);
        using var previous = await SendCoreAsync("SET", command, cancellationToken, RespireCommandFlags.None,
            allowReadFrom: true, cursorAffinity: null, observation: observation, observeErrors: false).ConfigureAwait(false);
        // Redis 7+ returns the existing winner even when NX refuses our write. A null reply
        // means our value was installed. There is no second GET/delete race or write replay.
        // Return serialized bytes even when we win so every caller deserializes its own
        // value instead of sharing the factory's mutable object with coalesced callers.
        return previous.IsNull ? RespValue.BulkString(bytes) : previous.ToOwned();
    }

    private static class CacheAsideType<T>
    {
        // Issued once per closed generic type and never recycled across operations or reconnects.
        // Generic statics distinguish runtime types, including identically named types
        // from separate load contexts. The shared command key can store this integer
        // without adding a Type field to every ordinary cached-command identity.
        internal static readonly long Identity = Interlocked.Increment(ref _nextCacheAsideTypeIdentity);
    }
}
