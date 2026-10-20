using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// The Respire client surface — implemented by <see cref="RespireClient"/> and its key-prefixed
/// views, and the type to inject and mock.
/// </summary>
public interface IRespireClient : IAsyncDisposable
{
    /// <summary>Client-side cache diagnostics, or null when caching is disabled.</summary>
    IRespireClientSideCache? ClientSideCache => null;

    /// <summary>Registers a fresh Redis tracking read for a string key or a hash field, discarding the reply.</summary>
    /// <remarks>
    /// Requires client-side tracking enabled on this view and a key covered by its tracking prefixes.
    /// Uses GET when <paramref name="field"/> is null, otherwise HGET. Applies the client's key prefix
    /// to the key, never the field. Always sends a separate read to Redis, even during mutations;
    /// neither consumes nor publishes cache entries and never coalesces with another read.
    /// Subscribe to invalidations before calling and register again after each invalidation.
    /// This does not reserve the key or guarantee tracking continuity across disconnects.
    /// Decorators must explicitly forward or implement this capability with their normal key routing
    /// and access policies. Implementations must not fall back to a cached read.
    /// </remarks>
    ValueTask RegisterKeyTrackingAsync(RespireKey key, RespireKey? field = null,
        CancellationToken cancellationToken = default);

    /// <summary>The configured data endpoint, or the latest validated primary for a Sentinel client.</summary>
    /// <remarks>Reading this property performs no network I/O. After a Sentinel handoff, a previously
    /// captured endpoint can be historical; host and port always come from one generation.</remarks>
    /// <exception cref="InvalidOperationException">A lazy Sentinel client has not resolved a validated
    /// primary yet. Use ConnectAsync or await the first command before reading its endpoint.</exception>
    RespireEndpoint Endpoint { get; }

    /// <summary>Whether at least one command connection is currently usable.</summary>
    bool IsConnected { get; }

    /// <summary>Raised when a command connection changes health.</summary>
    event Action<RespireConnectionStateChange>? ConnectionStateChanged;

    // Typed convenience facets, grouped by data type.
    /// <summary>String commands.</summary>
    IRespireStringCommands Strings { get; }
    /// <summary>Generic key commands.</summary>
    IRespireKeyCommands Keys { get; }
    /// <summary>Distributed lock commands.</summary>
    IRespireLockCommands Locks { get; }
    /// <summary>Hash commands.</summary>
    IRespireHashCommands Hashes { get; }
    /// <summary>List commands.</summary>
    IRespireListCommands Lists { get; }

    /// <summary>Redis sparse array commands.</summary>
    IRespireArrayCommands Arrays { get; }
    /// <summary>Set commands.</summary>
    IRespireSetCommands Sets { get; }
    /// <summary>Sorted-set commands.</summary>
    IRespireSortedSetCommands SortedSets { get; }
    /// <summary>Stream commands.</summary>
    IRespireStreamCommands Streams { get; }
    /// <summary>Bitmap commands.</summary>
    IRespireBitmapCommands Bitmaps { get; }
    /// <summary>HyperLogLog commands.</summary>
    IRespireHyperLogLogCommands HyperLogLog { get; }
    /// <summary>Geospatial commands.</summary>
    IRespireGeoCommands Geo { get; }
    /// <summary>Redis vector-set commands.</summary>
    IRespireVectorSetCommands VectorSets { get; }
    /// <summary>Lua script commands.</summary>
    IRespireScriptCommands Scripts { get; }

    /// <summary>Redis Functions (Redis 7+).</summary>
    IRespireFunctionCommands Functions { get; }
    /// <summary>Server administration and introspection commands.</summary>
    IRespireServerCommands Server { get; }

    // Root shortcuts for the most common operations.
    /// <summary>Gets a UTF-8 string, or null when the key is absent. Redis: GET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Gets and deserializes a value, or default when the key is absent. Redis: GET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a key's value deserialized as <typeparamref name="T"/> alongside a Found flag, so a
    /// missing key is distinguishable from a stored <c>default(T)</c>. Redis: GET.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireGetResult<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Reads a tracked value, or computes and conditionally stores it with a server expiry. Requires Redis 7+ and ClientSideCache.</summary>
    /// <remarks>
    /// Uses GET then SET NX GET. A concurrent writer wins; its value and TTL are preserved.
    /// Server support is not preflighted; an unsupported SET can fail after the factory runs.
    /// A null factory result is returned without storing it. Stored default values do not call the factory.
    /// Expiry must be a positive relative duration (at least one millisecond) or an absolute instant after
    /// the Unix epoch. None, Keep and Persist are rejected. Relative expiry starts at the successful SET;
    /// absolute expiry uses PXAT, even if the deadline has passed. Hits do not extend expiry.
    /// With CoalesceConcurrentMisses enabled, equivalent physical key, T and expiry form/value calls share the first
    /// factory and receive independently deserialized results. Invalidation can retire joining, so
    /// factories may overlap. Different clients never share factories; this is not distributed exactly-once execution.
    /// Each caller cancels only its own wait; the last caller leaving cancels a shared factory cooperatively.
    /// Throwing shared-token callbacks can fault remaining waiters but cannot interrupt client cleanup.
    /// Without coalescing each call uses its own factory and cancellation. CommandTimeout bounds Redis
    /// commands, not the factory. Do not recursively request the same shared identity from its factory.
    /// On a <see cref="WithoutClientCache"/> view the read goes to Redis and factories are never shared.
    /// Exceptions reach callers without automatic factory or accepted-write replay. A canceled accepted
    /// write may still execute. Results describe the successful read or atomic SET, not future state.
    /// Writes invalidate local entries; subsequent tracked reads repopulate them. Server expiry invalidates
    /// through normal tracking delivery; ClientSideCache.LocalExpiration is configured separately.
    /// </remarks>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetOrSetAsync<T>(RespireKey key, Func<CancellationToken, ValueTask<T?>> factory,
        RespireExpiry expiry, CancellationToken cancellationToken = default);

    /// <summary>Reads or refreshes a Redis-backed cache-aside entry with optional distributed single-flight and XFetch early refresh.</summary>
    /// <remarks>
    /// Uses a dedicated hash representation at key; do not mix with GET/SET or the overload without options.
    /// Requires scripting permissions; single-flight also requires CLIENT ID/KILL for ordered cancellation cleanup.
    /// Does not require ClientSideCache and always reads the primary.
    /// The hash stores the serialized payload, recompute duration, version and stale retention together.
    /// Contention waits are bounded by WaitTimeout; expiration of a crashed owner's lease allows recovery.
    /// Factories must honor cancellation and complete within LockTimeout. An expired owner cannot publish
    /// or release a successor's lease. Failover or lease expiry can still permit overlapping factories;
    /// use this for idempotent cache population, not exactly-once side effects.
    /// Each caller owns its cancellation. Null results and factory failures are not cached.
    /// Lease timeout throws TimeoutException; caller cancellation throws OperationCanceledException.
    /// A rejected publication returns a fresh winner or an independent copy of the computed value without caching it.
    /// Custom implementations must implement this capability explicitly.
    /// </remarks>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetOrSetAsync<T>(RespireKey key, RespireCacheAsideOptions options,
        Func<CancellationToken, ValueTask<T?>> factory, RespireExpiry expiry, CancellationToken cancellationToken = default);

    /// <summary>Gets raw bytes, or null when the key is absent. Redis: GET.</summary>
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Sets a protocol value with optional expiry and condition. Redis: SET.</summary>
    ValueTask<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Serializes and sets a value with optional expiry and condition. Redis: SET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes keys and returns how many existed. Redis: DEL.</summary>
    ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys);
    /// <summary>Deletes keys and returns how many existed. Redis: DEL.</summary>
    ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);
    /// <summary>Deletes one key with cancellation. Redis: DEL.</summary>
    [OverloadResolutionPriority(1)]
    ValueTask<long> DeleteAsync(RespireKey key, CancellationToken cancellationToken)
        => DeleteAsync([key], cancellationToken);

    /// <summary>Returns whether a key exists. Redis: EXISTS.</summary>
    ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Increments an integer and returns the new value. Redis: INCR/INCRBY.</summary>
    ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);
    /// <summary>Decrements an integer and returns the new value. Redis: DECR/DECRBY.</summary>
    ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);
    /// <summary>Sets or clears a key's expiry under an optional condition. Redis: PEXPIRE/PERSIST.</summary>
    ValueTask<bool> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        RespireExpireWhen when = RespireExpireWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Measures a PING round trip.</summary>
    ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default);

    // Pub/sub. Activation completes only after the server acknowledges the subscription.
    /// <summary>Publishes a message; sharded channel metadata selects SPUBLISH. Patterns cannot be published.</summary>
    ValueTask<long> PublishAsync(RespireChannel channel, RespireValue message, CancellationToken cancellationToken = default);

    /// <summary>Publishes a message with SPUBLISH. Patterns cannot be published.</summary>
    ValueTask<long> PublishShardedAsync(RespireChannel channel, RespireValue message, CancellationToken cancellationToken = default);

    /// <summary>Subscribes using the channel's explicit literal, pattern, or sharded kind, with optional buffer settings.</summary>
    /// <remarks>Strings convert implicitly to RespireChannel. Conversion snapshots bytes and validates UTF-16 before the call.</remarks>
    ValueTask<RespireSubscription> SubscribeAsync(
        RespireChannel channel, RespireSubscriptionOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to multiple channels using the channel's explicit literal, pattern, or sharded kind, with optional buffer settings.</summary>
    /// <remarks>All targets must have the same kind. Use a collection expression for multiple channels.</remarks>
    ValueTask<RespireSubscription> SubscribeAsync(
        ReadOnlySpan<RespireChannel> channels, RespireSubscriptionOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Subscribes using the pattern command family, with optional buffer settings.</summary>
    /// <remarks>Strings convert implicitly to RespireChannel. Conversion snapshots bytes and validates UTF-16 before the call.</remarks>
    ValueTask<RespireSubscription> SubscribePatternAsync(
        RespireChannel channel, RespireSubscriptionOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to multiple channels using the pattern command family, with optional buffer settings.</summary>
    /// <remarks>Targets use the pattern kind. Use a collection expression for multiple channels.</remarks>
    ValueTask<RespireSubscription> SubscribePatternAsync(
        ReadOnlySpan<RespireChannel> channels, RespireSubscriptionOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Subscribes using the sharded command family, with optional buffer settings.</summary>
    /// <remarks>Strings convert implicitly to RespireChannel. Conversion snapshots bytes and validates UTF-16 before the call.</remarks>
    ValueTask<RespireSubscription> SubscribeShardedAsync(
        RespireChannel channel, RespireSubscriptionOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to multiple channels using the sharded command family, with optional buffer settings.</summary>
    /// <remarks>Targets use the sharded kind. Use a collection expression for multiple channels.</remarks>
    ValueTask<RespireSubscription> SubscribeShardedAsync(
        ReadOnlySpan<RespireChannel> channels, RespireSubscriptionOptions options = default, CancellationToken cancellationToken = default);

    // Batches and transactions.
    /// <summary>Creates a transaction watching one key with cancellation. Redis: WATCH.</summary>
    [OverloadResolutionPriority(1)]
    ValueTask<RespireWatchedTransaction> CreateTransactionAsync(RespireKey watchKey, CancellationToken cancellationToken)
        => CreateTransactionAsync([watchKey], cancellationToken);

    /// <summary>Creates an explicit pipeline that queues commands until execution.</summary>
    RespireBatch CreateBatch();
    /// <summary>Creates an un-watched transaction.</summary>
    RespireTransaction CreateTransaction();

    /// <summary>Creates a transaction watching keys for optimistic concurrency. Redis: WATCH.</summary>
    ValueTask<RespireWatchedTransaction> CreateTransactionAsync(
        RespireKey[] watchKeys, CancellationToken cancellationToken = default);

    /// <summary>Creates a transaction watching keys for optimistic concurrency. Redis: WATCH.</summary>
    ValueTask<RespireWatchedTransaction> CreateTransactionAsync(params ReadOnlySpan<RespireKey> watchKeys);

    /// <summary>Creates a transaction watching keys with cancellation. Redis: WATCH.</summary>
    ValueTask<RespireWatchedTransaction> CreateTransactionAsync(
        ReadOnlySpan<RespireKey> watchKeys, CancellationToken cancellationToken);

    /// <summary>Sends a raw command with cancellation followed by individual arguments. The result uses GC-owned storage; disposal is optional.</summary>
    // Prefer the interpolated handler when a call has no separate raw arguments.
    [OverloadResolutionPriority(-1)]
    ValueTask<RespireResult> ExecuteAsync(RespireCommand command, CancellationToken cancellationToken, params RespireValue[] args)
        => ExecuteAsync(command, args, cancellationToken: cancellationToken);

    /// <summary>Queues a raw command with cancellation followed by individual arguments and discards its reply.</summary>
    [OverloadResolutionPriority(-1)]
    ValueTask ExecuteFireAndForgetAsync(RespireCommand command, CancellationToken cancellationToken, params RespireValue[] args)
        => ExecuteFireAndForgetAsync(command, args, cancellationToken);

    // Escape hatch. A string converts implicitly to RespireCommand. The params shape covers the
    // common case; the array shape adds flags and cancellation as optional arguments.

    /// <summary>
    /// Sends a command; each value is exactly one argument. The result uses GC-owned memory
    /// and disposal is optional.
    /// </summary>
    /// <remarks>Cluster execution validates all keys in known raw layouts before I/O. Unknown layouts remain server-validated.</remarks>
    ValueTask<RespireResult> ExecuteAsync(RespireCommand command, params RespireValue[] args);

    /// <summary>
    /// Sends a command with optional policy flags and cancellation. The result uses GC-owned
    /// memory and disposal is optional.
    /// </summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a command written as an interpolated string; each hole is one argument. The result uses
    /// GC-owned memory and disposal is optional.
    /// </summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireCommandInterpolatedStringHandler command,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a command and returns a result that must be disposed. Implementations may use pooled storage.</summary>
    ValueTask<RespireResult> ExecuteLeaseAsync(RespireCommand command, params RespireValue[] args)
        => ExecuteAsync(command, args);

    /// <summary>Sends a command with flags and cancellation and returns a result that must be disposed.</summary>
    ValueTask<RespireResult> ExecuteLeaseAsync(RespireCommand command, RespireValue[] args,
        RespireCommandFlags flags = RespireCommandFlags.None, CancellationToken cancellationToken = default)
        => ExecuteAsync(command, args, flags, cancellationToken);

    /// <summary>Sends an interpolated command and returns a result that must be disposed.</summary>
    ValueTask<RespireResult> ExecuteLeaseAsync(RespireCommandInterpolatedStringHandler command,
        RespireCommandFlags flags = RespireCommandFlags.None, CancellationToken cancellationToken = default)
        => ExecuteAsync(command, flags, cancellationToken);

    /// <summary>Queues a command and discards its reply.</summary>
    /// <remarks>Cluster redirects that cannot be followed are surfaced as server errors.</remarks>
    ValueTask ExecuteFireAndForgetAsync(RespireCommand command, params RespireValue[] args);

    /// <summary>Queues a command with cancellation and discards its reply.</summary>
    /// <remarks>Cluster redirects that cannot be followed are surfaced as server errors.</remarks>
    ValueTask ExecuteFireAndForgetAsync(
        RespireCommand command, RespireValue[] args, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues an interpolated command and discards its reply; each hole is one argument.
    /// Cluster redirects that cannot be followed are surfaced as server errors.
    /// </summary>
    ValueTask ExecuteFireAndForgetAsync(
        RespireCommandInterpolatedStringHandler command,
        CancellationToken cancellationToken = default);

    /// <summary>A view that prepends a prefix to every key; shares this client's connections.</summary>
    IRespireClient WithKeyPrefix(string prefix);

    /// <summary>A view that prepends an owned, binary-safe prefix to every key.</summary>
    /// <remarks>Empty prefixes are rejected. Channels are unchanged.</remarks>
    IRespireClient WithKeyPrefix(RespireKey prefix);

    /// <summary>Returns a view with a separate namespace for typed pub/sub publishes and subscriptions.</summary>
    /// <remarks>Appends to this view's pub/sub prefix without changing keys. Messages, subscription targets
    /// and matching patterns retain physical wire identity. Pattern subscriptions escape glob characters
    /// in the prefix. Notification descriptors, raw commands and administrative PUBSUB queries are physical
    /// and unchanged. Views share connections; disposing a view does not dispose the root client.</remarks>
    /// <exception cref="ArgumentException">The prefix is empty or contains an unpaired UTF-16 surrogate.</exception>
    /// <exception cref="ArgumentNullException">The prefix is null.</exception>
    IRespireClient WithPubSubPrefix(string prefix);

    /// <summary>Returns a view with an owned, binary-safe pub/sub prefix. Empty prefixes are rejected.</summary>
    IRespireClient WithPubSubPrefix(RespireKey prefix);

    /// <summary>Returns a view that applies a read-routing policy to metadata-confirmed read-only commands.</summary>
    /// <remarks>
    /// The view shares this client's connections. Only commands whose <see cref="RespireCommand.IsReadOnly"/>
    /// metadata is set and that have a routable key follow the policy; everything else stays on the primary.
    /// Custom implementations and decorators must implement this member; decorators should forward it
    /// to the wrapped client. Only catalogued read-only commands are routed by the policy.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="readFrom"/> is not a defined policy.</exception>
    /// <exception cref="InvalidOperationException">
    /// A non-primary policy was requested without Redis Cluster, Sentinel, or configured replica endpoints.
    /// </exception>
    /// <exception cref="NotSupportedException">This implementation does not support read routing views.</exception>
    IRespireClient WithReadFrom(RespireReadFrom readFrom);

    /// <summary>
    /// A view whose reads always go to Redis instead of the client-side cache, for reads that must
    /// observe the latest server value. Writes still invalidate cached entries.
    /// </summary>
    /// <remarks>
    /// The view shares this client's connections and keeps its key prefix and read routing; disposing
    /// it is a no-op. Returns the same client when client-side caching is disabled. Custom
    /// implementations and decorators must implement this member; decorators should forward it.
    /// </remarks>
    IRespireClient WithoutClientCache();
}
