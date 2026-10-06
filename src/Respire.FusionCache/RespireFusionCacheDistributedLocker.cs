using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Respire.Coordination;
using ZiggyCreatures.Caching.Fusion.Locking.Distributed;

namespace Respire.FusionCache;

/// <summary>FusionCache distributed locking on owner-checked Respire fenced leases, using a caller-owned client.</summary>
/// <remarks>
/// Supports RESP2 and RESP3 without tracking. Persistent counters must never be deleted, expired,
/// evicted, or reset. Redis failover can roll back counters. FusionCache's object-based interface
/// cannot fence arbitrary factory writes or cancel a factory when ownership is lost.
/// </remarks>
public sealed class RespireFusionCacheDistributedLocker : IFusionCacheDistributedLocker, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private readonly RespireCoordination _coordination;
    private readonly RespireFusionCacheDistributedLockerOptions _options;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly object _sync = new();
    private readonly HashSet<RespireFusionCacheLock> _handles = [];
    private TaskCompletionSource? _cleanup;

    /// <summary>Uses the existing client without creating connections or taking ownership of that client.</summary>
    public RespireFusionCacheDistributedLocker(IRespireClient client,
        RespireFusionCacheDistributedLockerOptions? options = null,
        ILogger<RespireFusionCacheDistributedLocker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _options = options ?? new();
        _options.Validate();
        _coordination = new(client);
        _logger = logger;
        _shutdownToken = _shutdown.Token;
    }

    /// <inheritdoc />
    public object? AcquireLock(string cacheName, string cacheInstanceId, string operationId,
        string key, string lockName, TimeSpan timeout, ILogger? logger, CancellationToken token)
        => AcquireLockAsync(cacheName, cacheInstanceId, operationId, key, lockName, timeout, logger, token)
            .AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask<object?> AcquireLockAsync(string cacheName, string cacheInstanceId, string operationId,
        string key, string lockName, TimeSpan timeout, ILogger? logger, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(cacheName);
        ArgumentNullException.ThrowIfNull(lockName);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        token.ThrowIfCancellationRequested();
        lock (_sync) ObjectDisposedException.ThrowIf(_cleanup is not null, this);
        var (leaseKey, counterKey) = CreateKeys(cacheName, lockName);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownToken);
        using var deadlineStop = new CancellationTokenSource();
        var deadline = timeout > TimeSpan.Zero ? CancelAtDeadlineAsync(waiting, timeout, deadlineStop.Token) : Task.CompletedTask;
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                var attempt = await _coordination.TryAcquireFencedLockAsync(leaseKey, counterKey,
                    _options.LeaseDuration, waiting.Token).ConfigureAwait(false);
                if (attempt.Acquired)
                {
                    RespireFusionCacheLock? handle = null;
                    var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownToken);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        waiting.Token.ThrowIfCancellationRequested();
                        var keepAlive = await attempt.Lock.KeepAliveAsync(lifetime.Token).ConfigureAwait(false);
                        handle = new(this, attempt.Lock, keepAlive, lifetime, logger ?? _logger);
                        lock (_sync)
                        {
                            ObjectDisposedException.ThrowIf(_cleanup is not null, this);
                            _handles.Add(handle);
                        }
                        handle.ObserveLifetime();
                        // A late cancellation cannot hand an unobserved renewing handle to FusionCache.
                        token.ThrowIfCancellationRequested();
                        waiting.Token.ThrowIfCancellationRequested();
                        return handle;
                    }
                    catch
                    {
                        if (handle is not null) await handle.DisposeAsync().ConfigureAwait(false);
                        else
                        {
                            lifetime.Dispose();
                            await attempt.DisposeAsync().ConfigureAwait(false);
                        }
                        throw;
                    }
                }
                if (timeout == TimeSpan.Zero) return null;
                var delay = _options.PollInterval;
                if (timeout != Timeout.InfiniteTimeSpan)
                {
                    var remaining = timeout - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero) return null;
                    if (remaining < delay) delay = remaining;
                }
                await Task.Delay(delay, waiting.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync) ObjectDisposedException.ThrowIf(_cleanup is not null, this);
            if (waiting.IsCancellationRequested) return null;
            throw;
        }
        finally
        {
            await deadlineStop.CancelAsync().ConfigureAwait(false);
            await deadline.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void ReleaseLock(string cacheName, string cacheInstanceId, string operationId,
        string key, string lockName, object? lockObj, ILogger? logger, CancellationToken token)
        => ReleaseLockAsync(cacheName, cacheInstanceId, operationId, key, lockName, lockObj, logger, token)
            .AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask ReleaseLockAsync(string cacheName, string cacheInstanceId, string operationId,
        string key, string lockName, object? lockObj, ILogger? logger, CancellationToken token)
    {
        if (lockObj is null) return;
        if (lockObj is not RespireFusionCacheLock handle || !handle.BelongsTo(this))
            throw new ArgumentException("The lock handle was not acquired by this locker.", nameof(lockObj));
        await handle.DisposeAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    internal void Forget(RespireFusionCacheLock handle)
    {
        lock (_sync) _handles.Remove(handle);
    }

    private static (RespireKey Lease, RespireKey Counter) CreateKeys(string cacheName, string lockName)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendIdentity(hash, cacheName);
        AppendIdentity(hash, lockName);
        var identity = Convert.ToHexString(hash.GetHashAndReset());
        // The dedicated hash tag is identical in both keys, including under client prefixes.
        var stem = "respire:fusioncache:lock:{" + identity + "}:";
        return (stem + "lease", stem + "counter");
    }

    private static void AppendIdentity(IncrementalHash hash, string value)
    {
        Span<byte> buffer = stackalloc byte[256];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value.Length);
        hash.AppendData(buffer[..4]);
        // Encode UTF-16 code units explicitly: no replacement fallback or platform byte order.
        for (var offset = 0; offset < value.Length;)
        {
            var count = Math.Min(buffer.Length / 2, value.Length - offset);
            for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16BigEndian(buffer[(i * 2)..], value[offset + i]);
            hash.AppendData(buffer[..(count * 2)]);
            offset += count;
        }
    }

    private static async Task CancelAtDeadlineAsync(CancellationTokenSource waiting, TimeSpan timeout, CancellationToken stop)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining, stop).ConfigureAwait(false);
            }
            await waiting.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    /// <summary>Stops and joins all owned handles without disposing the caller's client.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        RespireFusionCacheLock[] handles;
        lock (_sync)
        {
            if (_cleanup is not null) return new ValueTask(_cleanup.Task);
            completion = _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handles = [.. _handles];
        }
        _ = CompleteCleanupAsync(completion, handles);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource completion, RespireFusionCacheLock[] handles)
    {
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            foreach (var handle in handles) await handle.DisposeAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally { _shutdown.Dispose(); }
    }

    /// <summary>Synchronously stops and joins all owned handles, preserving the caller's client.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
