using System.Security.Cryptography;

namespace Respire.Extensions.Coordination;

/// <summary>Bounds work against each independent Redis node in a Redlock attempt.</summary>
public sealed record RespireRedlockOptions
{
    /// <summary>Maximum time to wait for one node operation. Default is one second.</summary>
    public TimeSpan NodeTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Fraction of the lease subtracted for clock drift. Default is 0.01.</summary>
    public double DriftFactor { get; init; } = 0.01;
}

/// <summary>A result from an immediate Redlock acquisition attempt.</summary>
public readonly struct RespireRedlockAttempt : IAsyncDisposable
{
    private readonly RespireRedlock? _lock;

    internal RespireRedlockAttempt(RespireRedlock? @lock) => _lock = @lock;

    /// <summary>Whether a quorum acquired the lease with positive remaining validity.</summary>
    public bool Acquired => _lock is not null;

    /// <summary>The acquired lease. Throws when this attempt did not acquire a quorum.</summary>
    public RespireRedlock Lock => _lock ?? throw new InvalidOperationException("Redlock quorum was not acquired.");

    /// <summary>Releases the acquired lease, or does nothing when acquisition failed.</summary>
    public ValueTask DisposeAsync() => _lock?.DisposeAsync() ?? default;
}

/// <summary>Runs Redlock attempts across independently configured standalone Redis clients.</summary>
/// <remarks>
/// Every node must represent an independent Redis deployment. The group never owns or disposes
/// supplied clients. Redlock does not provide consensus fencing; asynchronous replication,
/// failover, partitions, and clock drift can violate mutual exclusion. Use fencing tokens when
/// the protected resource can enforce them.
/// </remarks>
public sealed class RespireRedlockGroup
{
    private const int TokenLength = 32;
    private readonly IRespireClient[] _clients;
    private readonly RespireRedlockOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>Creates a Redlock group from an odd set of at least three independent clients.</summary>
    public RespireRedlockGroup(
        IEnumerable<IRespireClient> clients,
        RespireRedlockOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        _clients = clients.ToArray();
        if (_clients.Length < 3 || (_clients.Length & 1) == 0)
            throw new ArgumentException("Redlock requires an odd number of at least three independent clients.", nameof(clients));
        if (_clients.Any(static client => client is null))
            throw new ArgumentException("Redlock clients cannot contain null.", nameof(clients));
        if (_clients.Distinct(ReferenceEqualityComparer.Instance).Count() != _clients.Length)
            throw new ArgumentException("Each Redlock node requires a distinct client instance.", nameof(clients));

        _options = options ?? new RespireRedlockOptions();
        if (_options.NodeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "NodeTimeout must be positive.");
        if (!double.IsFinite(_options.DriftFactor) || _options.DriftFactor < 0 || _options.DriftFactor >= 1)
            throw new ArgumentOutOfRangeException(nameof(options), "DriftFactor must be finite and in [0, 1).");
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Immediately tries to acquire a lease on a quorum of independent Redis nodes.</summary>
    /// <param name="key">Lock key, before each client's configured key prefix.</param>
    /// <param name="duration">Positive lease duration, at least one millisecond.</param>
    /// <param name="cancellationToken">Cancels node operations; uncertain acquisitions are cleaned up best-effort.</param>
    /// <remarks>
    /// The returned validity subtracts elapsed acquisition time and the configured drift allowance
    /// plus two milliseconds. A failed or uncertain attempt releases its token from every node;
    /// any node that cannot be reached relies on its bounded expiry.
    /// </remarks>
    public async ValueTask<RespireRedlockAttempt> TryAcquireAsync(
        RespireKey key,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (duration.Ticks / TimeSpan.TicksPerMillisecond < 1)
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be at least one millisecond.");

        var tokenBytes = RandomNumberGenerator.GetBytes(TokenLength);
        var token = new RespireLockToken(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        var started = _clock.GetTimestamp();
        bool[] acquired;
        try
        {
            acquired = await RunOnNodesAsync(
                (client, cancellation) => client.Locks.TryTakeAsync(key, token, duration, cancellation),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseEverywhereAsync(key, token).ConfigureAwait(false);
            throw;
        }

        var completed = _clock.GetTimestamp();
        var validity = CalculateValidity(duration, _clock.GetElapsedTime(started, completed), _options.DriftFactor);
        if (acquired.Count(static success => success) >= Quorum && validity > TimeSpan.Zero)
        {
            return new RespireRedlockAttempt(new RespireRedlock(
                _clients, key, token, duration, validity, completed, Quorum, _options, _clock));
        }

        await ReleaseEverywhereAsync(key, token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }

    private int Quorum => _clients.Length / 2 + 1;

    internal static TimeSpan CalculateValidity(TimeSpan duration, TimeSpan elapsed, double driftFactor)
    {
        var driftTicks = decimal.Ceiling((decimal)duration.Ticks * (decimal)driftFactor)
            + TimeSpan.TicksPerMillisecond * 2m;
        var remainingTicks = duration.Ticks - (decimal)elapsed.Ticks - driftTicks;
        return remainingTicks > 0 ? TimeSpan.FromTicks((long)remainingTicks) : TimeSpan.Zero;
    }

    internal async ValueTask<bool[]> RunOnNodesAsync(
        Func<IRespireClient, CancellationToken, ValueTask<bool>> operation,
        CancellationToken cancellationToken)
    {
        var pending = new Task<bool>[_clients.Length];
        for (var i = 0; i < _clients.Length; i++)
            pending[i] = RunOnNodeAsync(_clients[i], operation, cancellationToken);
        return await Task.WhenAll(pending).ConfigureAwait(false);
    }

    internal async ValueTask<bool[]> ReleaseEverywhereAsync(RespireKey key, RespireLockToken token)
        => await RunOnNodesAsync(
            (client, cancellation) => client.Locks.ReleaseAsync(key, token, cancellation),
            CancellationToken.None).ConfigureAwait(false);

    private async Task<bool> RunOnNodeAsync(
        IRespireClient client,
        Func<IRespireClient, CancellationToken, ValueTask<bool>> operation,
        CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(_options.NodeTimeout);
        Task<bool>? command = null;
        try
        {
            command = operation(client, timeout.Token).AsTask();
            return await command.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            Observe(command);
            throw;
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            Observe(command);
            return false;
        }
    }

    private static void Observe(Task<bool>? task)
    {
        if (task is null || task.IsCompleted) return;
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

/// <summary>A Redlock lease acquired on a quorum; operations remain attached to supplied clients.</summary>
public sealed class RespireRedlock : IAsyncDisposable
{
    private readonly IRespireClient[] _clients;
    private readonly RespireRedlockOptions _options;
    private readonly TimeProvider _clock;
    private readonly int _quorum;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _validUntil;
    private long _validityTicks;
    private long _durationTicks;
    private int _released;

    internal RespireRedlock(
        IRespireClient[] clients, RespireKey key, RespireLockToken token,
        TimeSpan duration, TimeSpan validity, long started, int quorum,
        RespireRedlockOptions options, TimeProvider clock)
    {
        _clients = clients;
        Key = key.Snapshot();
        Token = token;
        _durationTicks = duration.Ticks;
        _validityTicks = validity.Ticks;
        _validUntil = AddTimestampDuration(started, validity, clock.TimestampFrequency);
        _quorum = quorum;
        _options = options;
        _clock = clock;
    }

    /// <summary>Lock key before each client's configured key prefix.</summary>
    public RespireKey Key { get; }
    /// <summary>Random binary owner value stored on every node in the acquired quorum.</summary>
    public RespireLockToken Token { get; }
    /// <summary>Configured lease duration.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));
    /// <summary>Validity after acquisition elapsed time and configured drift allowance.</summary>
    public TimeSpan Validity => TimeSpan.FromTicks(Interlocked.Read(ref _validityTicks));
    /// <summary>Conservative estimate; it does not prove continued server ownership.</summary>
    public TimeSpan RemainingEstimate
    {
        get
        {
            if (Volatile.Read(ref _released) != 0) return TimeSpan.Zero;
            var remaining = _clock.GetElapsedTime(_clock.GetTimestamp(), Interlocked.Read(ref _validUntil));
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    /// <summary>Whether this handle was released, expired locally, or lost quorum.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0 || RemainingEstimate == TimeSpan.Zero;

    /// <summary>Renews this token on a quorum and recomputes validity from the new attempt.</summary>
    public async ValueTask<bool> ResetExpiryAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration.Ticks / TimeSpan.TicksPerMillisecond < 1)
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be at least one millisecond.");
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReleased) return false;
            var started = _clock.GetTimestamp();
            bool[] renewed;
            try
            {
                renewed = await RunOnNodesAsync(
                    (client, token) => client.Locks.ResetExpiryAsync(Key, Token, duration, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await LoseOwnershipAsync().ConfigureAwait(false);
                throw;
            }

            var completed = _clock.GetTimestamp();
            var validity = RespireRedlockGroup.CalculateValidity(duration, _clock.GetElapsedTime(started, completed), _options.DriftFactor);
            if (renewed.Count(static success => success) >= _quorum && validity > TimeSpan.Zero)
            {
                Interlocked.Exchange(ref _durationTicks, duration.Ticks);
                Interlocked.Exchange(ref _validityTicks, validity.Ticks);
                Interlocked.Exchange(ref _validUntil, AddTimestampDuration(completed, validity, _clock.TimestampFrequency));
                return true;
            }

            await LoseOwnershipAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases this token from every node; returns true when a quorum confirmed release.</summary>
    public async ValueTask<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _released) != 0) return false;
            Interlocked.Exchange(ref _released, 1);
            var released = await RunOnNodesAsync(
                (client, token) => client.Locks.ReleaseAsync(Key, Token, token),
                cancellationToken).ConfigureAwait(false);
            return released.Count(static success => success) >= _quorum;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases the lease on a best-effort basis.</summary>
    public async ValueTask DisposeAsync()
    {
        try { _ = await ReleaseAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async ValueTask LoseOwnershipAsync()
    {
        Interlocked.Exchange(ref _released, 1);
        _ = await RunOnNodesAsync(
            (client, token) => client.Locks.ReleaseAsync(Key, Token, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<bool[]> RunOnNodesAsync(
        Func<IRespireClient, CancellationToken, ValueTask<bool>> operation,
        CancellationToken cancellationToken)
    {
        var pending = new Task<bool>[_clients.Length];
        for (var i = 0; i < _clients.Length; i++)
            pending[i] = RunOnNodeAsync(_clients[i], operation, cancellationToken);
        return await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private static long AddTimestampDuration(long timestamp, TimeSpan duration, long timestampFrequency)
        => checked(timestamp + (long)((decimal)duration.Ticks * timestampFrequency / TimeSpan.TicksPerSecond));

    private async Task<bool> RunOnNodeAsync(
        IRespireClient client,
        Func<IRespireClient, CancellationToken, ValueTask<bool>> operation,
        CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(_options.NodeTimeout);
        Task<bool>? command = null;
        try
        {
            command = operation(client, timeout.Token).AsTask();
            return await command.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            Observe(command);
            throw;
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            Observe(command);
            return false;
        }
    }

    private static void Observe(Task<bool>? task)
    {
        if (task is null || task.IsCompleted) return;
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
