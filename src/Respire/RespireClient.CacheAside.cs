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
        TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var milliseconds = ttl.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be at least one millisecond.");
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var cache = _core.ClientCache
            ?? throw new InvalidOperationException("GetOrSetAsync requires ClientSideCache to be enabled.");
        var resolvedKey = ResolveKey(key);
        if (cache.TryGet(in resolvedKey, out var cached) && !cached.IsNull)
            return new ValueTask<T?>(DeserializeBorrowed<T>(in cached));

        return GetOrSetMissAsync(resolvedKey.Snapshot(), factory, milliseconds, cache, cancellationToken);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<T?> GetOrSetMissAsync<T>(RespireKey key, Func<CancellationToken, ValueTask<T?>> factory,
        long milliseconds, ClientSideCacheCoordinator cache, CancellationToken cancellationToken)
    {
        // A factory is not a GET producer. Types and TTLs with different contracts never join.
        // Within one identity the first factory wins; callers must agree on its meaning.
        using var response = cache.CoalesceConcurrentMisses
            ? await cache.CoalesceReadAsync(
                new ClientCacheCommandKey("GETORSET", key.AsValue(), CacheAsideType<T>.Identity, milliseconds),
                (Client: this, Key: key, Factory: factory, Milliseconds: milliseconds),
                static (state, token) => state.Client.ProduceCacheAsideAsync(state.Key, state.Factory, state.Milliseconds, token),
                cancellationToken).ConfigureAwait(false)
            : await ProduceCacheAsideAsync(key, factory, milliseconds, cancellationToken).ConfigureAwait(false);
        return DeserializeBorrowed<T>(in response);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<RespValue> ProduceCacheAsideAsync<T>(RespireKey key,
        Func<CancellationToken, ValueTask<T?>> factory, long milliseconds, CancellationToken cancellationToken)
    {
        // Use normal tracked reads and their insertion fences. Never insert a SET reply into
        // the cache: the write can invalidate tracking, and another writer may already follow it.
        // The public entry already counted this caller's lookup. Recheck without counting
        // again because another producer may have filled the cache before this one starts.
        var cache = _core.ClientCache!;
        var existing = cache.TryPeek(in key, out var cached)
            ? cached.ToOwned()
            : await GetAndCacheAsync(key, cache, cancellationToken,
                static (RespireClient _, in RespValue value) => value.ToOwned()).ConfigureAwait(false);
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
        var command = new SetCommand(key.AsValue(), bytes, TimeSpan.FromMilliseconds(milliseconds), SetWhen.NotExists, returnOld: true);
        using var previous = await SendAsync("SET", command, cancellationToken).ConfigureAwait(false);
        // Redis 7+ returns the existing winner even when NX refuses our write. A null reply
        // means our value was installed. There is no second GET/delete race or write replay.
        // Return serialized bytes even when we win so every caller deserializes its own
        // value instead of sharing the factory's mutable object with coalesced callers.
        return previous.IsNull ? RespValue.BulkString(bytes) : previous.ToOwned();
    }

    private static class CacheAsideType<T>
    {
        // Generic statics distinguish runtime types, including identically named types
        // from separate load contexts. The shared command key can store this integer
        // without adding a Type field to every ordinary cached-command identity.
        internal static readonly long Identity = Interlocked.Increment(ref _nextCacheAsideTypeIdentity);
    }
}
