namespace Respire;

/// <summary>
/// A typed Redis hash backed by the client's server-assisted near cache, with optional
/// read-through loading and write-through or batched write-behind persistence.
/// </summary>
/// <remarks>
/// Enable ClientSideCache on the supplied client and include the map's physical key in its
/// cache prefixes. Values are decoded from cached bytes on every read, so mutable returned
/// objects cannot corrupt the local cache. Operations and callbacks are serialized per map;
/// callbacks must not reenter it. Dispose the map before disposing its client.
/// </remarks>
public sealed class RespireMap<TKey, TValue> : IAsyncDisposable where TKey : notnull
{
    // Touch a collision-free temporary field atomically. This invalidates tracked hash reads
    // even for an empty map, without reserving field names or changing existing field TTLs.
    private static readonly RespireScript ClearScript = RespireScript.Create("""
        local field = ARGV[1]
        while redis.call('HEXISTS', KEYS[1], field) == 1 do field = field .. ':' end
        redis.call('HSET', KEYS[1], field, '')
        redis.call('HDEL', KEYS[1], field)
        return 1
        """);

    private readonly IRespireClient _client;
    private readonly RespireKey _key;
    private readonly Func<TKey, string> _formatKey;
    private readonly Func<TValue, byte[]> _encode;
    private readonly Func<ReadOnlyMemory<byte>, TValue> _decode;
    private readonly RespireMapOptions<TKey, TValue> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly Queue<RespireMapChange<TKey, TValue>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private Exception? _lastWriterError;
    private int _stopping;
    private bool _disposed;

    /// <summary>
    /// Creates a map without taking ownership of the client. Key formatting must be stable
    /// and injective. Explicit byte codecs support Native AOT and binary payloads.
    /// </summary>
    public RespireMap(IRespireClient client, RespireKey key, Func<TKey, string> formatKey,
        Func<TValue, byte[]> encode, Func<ReadOnlyMemory<byte>, TValue> decode,
        RespireMapOptions<TKey, TValue>? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(formatKey);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(decode);
        if (client.ClientSideCache is null)
            throw new ArgumentException("A near-cache map requires ClientSideCache to be enabled.", nameof(client));
        _options = options ?? new();
        _options.Validate();
        _client = client.WithReadFrom(RespireReadFrom.Primary);
        _key = key.Snapshot();
        _formatKey = formatKey;
        _encode = encode;
        _decode = decode;
        _worker = _options.WriteBehind ? RunWriterAsync() : Task.CompletedTask;
    }

    /// <summary>The most recent writer failure; cleared after a successful writer call.</summary>
    public Exception? LastWriterError => Volatile.Read(ref _lastWriterError);

    /// <summary>Reads from the near cache or Redis, then optionally loads an absent field.</summary>
    public async ValueTask<RespireGet<TValue>> GetAsync(TKey key, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var field = Format(key);
            var bytes = await _client.Hashes.GetBytesAsync(_key, field, cancellationToken).ConfigureAwait(false);
            if (bytes is not null) return new(true, _decode(bytes));
            if (_options.Loader is null) return default;
            var loaded = await _options.Loader(key, cancellationToken).ConfigureAwait(false);
            if (!loaded.Found) return default;
            // Never replace a concurrent writer's value, and never echo a loader fill to its writer.
            await StoreAsync(field, Encode(loaded.Value!), SetWhen.NotExists,
                _options.EntryExpiration, cancellationToken).ConfigureAwait(false);
            bytes = await _client.Hashes.GetBytesAsync(_key, field, cancellationToken).ConfigureAwait(false);
            return bytes is null ? default : new(true, _decode(bytes));
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Stores a value in Redis, then persists it through the writer. An explicit lifetime overrides
    /// EntryExpiration; absent either, HSET removes any previous field expiry.
    /// </summary>
    public async ValueTask SetAsync(TKey key, TValue value, TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        expiration ??= _options.EntryExpiration;
        RespireMapOptions<TKey, TValue>.ValidateExpiration(expiration);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var field = Format(key);
            var bytes = Encode(value);
            // Decode an owned snapshot for delayed persistence, not the caller's mutable value.
            var change = _options.Writer is null ? default : new RespireMapChange<TKey, TValue>(key, new(true, _decode(bytes)));
            await MakeRoomAsync(cancellationToken).ConfigureAwait(false);
            await StoreAsync(field, bytes, SetWhen.Always, expiration, cancellationToken).ConfigureAwait(false);
            await PersistAsync(change, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Removes a field and forwards the deletion to the writer, even if Redis already lacked it.
    /// Returns whether Redis removed an entry.
    /// </summary>
    public async ValueTask<bool> RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var field = Format(key);
            await MakeRoomAsync(cancellationToken).ConfigureAwait(false);
            var removed = await _client.Hashes.RemoveAsync(_key, [field], cancellationToken).ConfigureAwait(false);
            await PersistAsync(new(key, default), cancellationToken).ConfigureAwait(false);
            return removed != 0;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Warms requested entries, using the loader on misses. Returns the number found.</summary>
    public async ValueTask<int> PreloadAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var found = 0;
        foreach (var key in keys)
            if ((await GetAsync(key, cancellationToken).ConfigureAwait(false)).Found) found++;
        return found;
    }

    /// <summary>Scans Redis and warms existing fields without callbacks. Requires Redis 7.4+.</summary>
    public async ValueTask PreloadAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var field in _client.Hashes.ScanFieldsAsync(_key, cancellationToken: cancellationToken).ConfigureAwait(false))
                await _client.Hashes.GetBytesAsync(_key, field, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Invalidates this hash's near-cache views across clients through Redis tracking. Delivery to
    /// remote clients is asynchronous. Redis entries and their expiry metadata are preserved.
    /// </summary>
    public async ValueTask ClearLocalCachesAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var result = await _client.Scripts.ExecuteAsync(ClearScript, [_key],
                ["__respire_map_clear:" + Guid.NewGuid().ToString("N")], cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drains queued writer batches. Failures retain the failed batch and propagate to the caller.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Stops admission, waits for accepted operations, and drains every queued write. A writer failure
    /// is propagated with its batch retained; retry DisposeAsync after fixing the writer. No writes
    /// are durable across a process crash. The supplied client remains usable.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _stopping, 1);
            await _stop.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false);
                _disposed = true;
                _stop.Dispose();
            }
            finally { _gate.Release(); }
        }
        finally { _disposeGate.Release(); }
    }

    private async ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _stopping) == 0) return;
        _gate.Release();
        throw new ObjectDisposedException(GetType().Name);
    }

    private string Format(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _formatKey(key) ?? throw new InvalidOperationException("The key formatter returned null.");
    }

    private byte[] Encode(TValue value)
        => (_encode(value) ?? throw new InvalidOperationException("The value encoder returned null.")).ToArray();

    private async ValueTask StoreAsync(string field, byte[] value, SetWhen when,
        TimeSpan? expiration, CancellationToken cancellationToken)
    {
        if (expiration is { } lifetime)
            await _client.Hashes.SetExpireAsync(_key, lifetime, when, [(field, (RespireValue)value)], cancellationToken).ConfigureAwait(false);
        else
            await _client.Hashes.SetAsync(_key, field, (RespireValue)value, when, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask MakeRoomAsync(CancellationToken cancellationToken)
    {
        if (_options.WriteBehind && _pending.Count >= _options.MaxPendingWrites)
            await FlushBatchAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PersistAsync(RespireMapChange<TKey, TValue> change, CancellationToken cancellationToken)
    {
        if (_options.Writer is null) return;
        if (_options.WriteBehind)
            _pending.Enqueue(change); // No cancellation point after a successful Redis write.
        else
            await CallWriterAsync([change], cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count != 0)
            await FlushBatchAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FlushBatchAsync(CancellationToken cancellationToken)
    {
        var batch = _pending.Take(_options.BatchSize).ToArray();
        await CallWriterAsync(Array.AsReadOnly(batch), cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < batch.Length; i++) _pending.Dequeue();
    }

    private async ValueTask CallWriterAsync(IReadOnlyList<RespireMapChange<TKey, TValue>> batch, CancellationToken cancellationToken)
    {
        try
        {
            await _options.Writer!(batch, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastWriterError, null);
        }
        catch (Exception error)
        {
            Volatile.Write(ref _lastWriterError, error);
            throw;
        }
    }

    private async Task RunWriterAsync()
    {
        using var timer = new PeriodicTimer(_options.FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    // Shutdown does not cancel a writer batch already in progress.
                    await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception) { /* LastWriterError records the failure; the batch remains queued. */ }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
}
