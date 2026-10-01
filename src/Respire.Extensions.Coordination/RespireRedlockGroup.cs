using System.Security.Cryptography;

namespace Respire.Extensions.Coordination;

/// <summary>Bounds work against each independent Redis node in a Redlock attempt.</summary>
public sealed record RespireRedlockOptions
{
    /// <summary>
    /// Maximum time to wait for one node operation. Default is one second. Must be positive and at
    /// most 4,294,967,294 milliseconds (about 49.7 days), the longest supported timer delay.
    /// </summary>
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
    private readonly RespireRedlockNodes _nodes;

    /// <summary>Creates a Redlock group from an odd set of at least three independent clients.</summary>
    public RespireRedlockGroup(
        IEnumerable<IRespireClient> clients,
        RespireRedlockOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var nodes = clients.ToArray();
        if (nodes.Length < 3 || (nodes.Length & 1) == 0)
            throw new ArgumentException("Redlock requires an odd number of at least three independent clients.", nameof(clients));
        if (nodes.Any(static client => client is null))
            throw new ArgumentException("Redlock clients cannot contain null.", nameof(clients));
        if (nodes.Distinct(ReferenceEqualityComparer.Instance).Count() != nodes.Length)
            throw new ArgumentException("Each Redlock node requires a distinct client instance.", nameof(clients));

        options ??= new RespireRedlockOptions();
        if (options.NodeTimeout <= TimeSpan.Zero || options.NodeTimeout > RespireRedlockNodes.MaxNodeTimeout)
            throw new ArgumentOutOfRangeException(nameof(options), "NodeTimeout must be positive and at most 4,294,967,294 milliseconds.");
        if (!double.IsFinite(options.DriftFactor) || options.DriftFactor < 0 || options.DriftFactor >= 1)
            throw new ArgumentOutOfRangeException(nameof(options), "DriftFactor must be finite and in [0, 1).");
        _nodes = new RespireRedlockNodes(nodes, options, timeProvider ?? TimeProvider.System);
    }

    /// <summary>Immediately tries to acquire a lease on a quorum of independent Redis nodes.</summary>
    /// <param name="key">Lock key, before each client's configured key prefix.</param>
    /// <param name="duration">Positive lease duration, at least one millisecond.</param>
    /// <param name="cancellationToken">Cancels node operations; uncertain acquisitions are cleaned up best-effort.</param>
    /// <remarks>
    /// The returned validity subtracts elapsed acquisition time and the configured drift allowance
    /// plus two milliseconds. A failed or uncertain attempt releases its token from every node;
    /// any node that cannot be reached relies on its bounded expiry. A node that timed out can
    /// still apply the acquisition after that cleanup, and then holds the token until expiry.
    /// </remarks>
    public async ValueTask<RespireRedlockAttempt> TryAcquireAsync(
        RespireKey key,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RespireRedlockNodes.ValidateDuration(duration, nameof(duration));

        // One owned copy: every node, the handle, and cleanup must target the same key bytes.
        key = key.Snapshot();
        var tokenBytes = RandomNumberGenerator.GetBytes(TokenLength);
        var token = new RespireLockToken(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        var started = _nodes.Clock.GetTimestamp();
        _nodes.ValidateDeadline(started, duration, nameof(duration));
        bool[] acquired;
        try
        {
            acquired = await _nodes.RunAsync(
                (client, cancellation) => client.Locks.TryTakeAsync(key, token, duration, cancellation),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _nodes.ReleaseAsync(key, token).ConfigureAwait(false);
            throw;
        }

        if (_nodes.TryCreateLease(acquired, duration, started) is { } lease)
            return new RespireRedlockAttempt(new RespireRedlock(_nodes, key, token, lease));

        await _nodes.ReleaseAsync(key, token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }
}

/// <summary>Shared node fan-out, quorum, and timing rules for a group and its leases.</summary>
internal sealed class RespireRedlockNodes
{
    /// <summary>Largest per-node timeout supported by <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>.</summary>
    internal static readonly TimeSpan MaxNodeTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly IRespireClient[] _clients;
    private readonly TimeSpan _nodeTimeout;
    private readonly double _driftFactor;

    internal RespireRedlockNodes(IRespireClient[] clients, RespireRedlockOptions options, TimeProvider clock)
    {
        _clients = clients;
        _nodeTimeout = options.NodeTimeout;
        _driftFactor = options.DriftFactor;
        Clock = clock;
        Quorum = clients.Length / 2 + 1;
    }

    internal TimeProvider Clock { get; }

    internal int Quorum { get; }

    internal static void ValidateDuration(TimeSpan duration, string parameterName)
    {
        if (duration.Ticks / TimeSpan.TicksPerMillisecond < 1)
            throw new ArgumentOutOfRangeException(parameterName, "Lease duration must be at least one millisecond.");
    }

    /// <summary>Rejects a lease whose local deadline cannot be represented, before any node is contacted.</summary>
    internal void ValidateDeadline(long started, TimeSpan duration, string parameterName)
    {
        if (!TryAddDuration(started, duration, out _))
            throw new ArgumentOutOfRangeException(parameterName, "Lease duration is too long to track locally.");
    }

    /// <summary>
    /// Builds the lease state when a quorum succeeded with positive validity. The validity and the
    /// deadline share one completion timestamp, so the deadline equals the start plus the
    /// duration less drift, however long the process paused between samples.
    /// </summary>
    internal RespireRedlockLease? TryCreateLease(bool[] results, TimeSpan duration, long started)
    {
        if (results.Count(static success => success) < Quorum) return null;
        var completed = Clock.GetTimestamp();
        var validity = CalculateValidity(duration, Clock.GetElapsedTime(started, completed), _driftFactor);
        if (validity <= TimeSpan.Zero) return null;
        // ValidateDeadline proved started + duration fits; completed + validity cannot exceed it.
        var validUntil = TryAddDuration(completed, validity, out var deadline) ? deadline : long.MaxValue;
        // A coarse clock can truncate a small positive validity to zero ticks; such a lease would
        // already read as expired, so it is not reported as acquired.
        if (validUntil <= completed) return null;
        return new RespireRedlockLease(duration, validity, validUntil);
    }

    internal static TimeSpan CalculateValidity(TimeSpan duration, TimeSpan elapsed, double driftFactor)
    {
        var driftTicks = decimal.Ceiling((decimal)duration.Ticks * (decimal)driftFactor)
            + TimeSpan.TicksPerMillisecond * 2m;
        var remainingTicks = duration.Ticks - (decimal)elapsed.Ticks - driftTicks;
        return remainingTicks > 0 ? TimeSpan.FromTicks((long)remainingTicks) : TimeSpan.Zero;
    }

    internal async ValueTask<bool[]> RunAsync(
        Func<IRespireClient, CancellationToken, ValueTask<bool>> operation,
        CancellationToken cancellationToken)
    {
        var pending = new Task<bool>[_clients.Length];
        for (var i = 0; i < _clients.Length; i++)
            pending[i] = RunOnNodeAsync(_clients[i], operation, cancellationToken);
        return await Task.WhenAll(pending).ConfigureAwait(false);
    }

    /// <summary>Releases a token everywhere; each node wait is bounded by the node timeout, not the caller.</summary>
    internal async ValueTask<bool> ReleaseAsync(RespireKey key, RespireLockToken token)
    {
        var released = await RunAsync(
            async (client, cancellation) =>
            {
                _ = await client.Locks.ReleaseAsync(key, token, cancellation).ConfigureAwait(false);
                return true;
            },
            CancellationToken.None).ConfigureAwait(false);
        return released.Count(static success => success) >= Quorum;
    }

    private bool TryAddDuration(long timestamp, TimeSpan duration, out long deadline)
    {
        try
        {
            deadline = checked(timestamp + (long)((decimal)duration.Ticks * Clock.TimestampFrequency / TimeSpan.TicksPerSecond));
            return true;
        }
        catch (OverflowException)
        {
            deadline = 0;
            return false;
        }
    }

    // A failing, timed-out, or disposed node counts as unavailable, as the Redlock algorithm
    // prescribes; only caller cancellation escapes so uncertain attempts can be cleaned up.
    private async Task<bool> RunOnNodeAsync(
        IRespireClient client,
        Func<IRespireClient, CancellationToken, ValueTask<bool>> operation,
        CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(_nodeTimeout);
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

/// <summary>One immutable lease generation, published atomically so readers never mix renewals.</summary>
internal sealed record RespireRedlockLease(TimeSpan Duration, TimeSpan Validity, long ValidUntil);

/// <summary>A Redlock lease acquired on a quorum; operations remain attached to supplied clients.</summary>
public sealed class RespireRedlock : IAsyncDisposable
{
    private readonly RespireRedlockNodes _nodes;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private RespireRedlockLease _lease;
    private int _released;
    private int _releaseConfirmed;

    internal RespireRedlock(RespireRedlockNodes nodes, RespireKey key, RespireLockToken token, RespireRedlockLease lease)
    {
        _nodes = nodes;
        Key = key;
        Token = token;
        _lease = lease;
    }

    /// <summary>Lock key before each client's configured key prefix.</summary>
    public RespireKey Key { get; }
    /// <summary>Random binary owner value stored on every node in the acquired quorum.</summary>
    public RespireLockToken Token { get; }
    /// <summary>Configured lease duration.</summary>
    public TimeSpan Duration => Volatile.Read(ref _lease).Duration;
    /// <summary>Validity after acquisition elapsed time and configured drift allowance.</summary>
    public TimeSpan Validity => Volatile.Read(ref _lease).Validity;
    /// <summary>Conservative estimate; it does not prove continued server ownership.</summary>
    public TimeSpan RemainingEstimate
    {
        get
        {
            if (Volatile.Read(ref _released) != 0) return TimeSpan.Zero;
            var clock = _nodes.Clock;
            var remaining = clock.GetElapsedTime(clock.GetTimestamp(), Volatile.Read(ref _lease).ValidUntil);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    /// <summary>Whether this handle was released, expired locally, or lost quorum.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0 || RemainingEstimate == TimeSpan.Zero;

    /// <summary>Renews this token on a quorum and recomputes validity from the new attempt.</summary>
    /// <remarks>
    /// Returns <see langword="false"/> without contacting nodes when the handle is already released
    /// or locally expired. When a renewal is not confirmed on a quorum with positive validity,
    /// including node timeouts and caller cancellation, the handle is marked released and the
    /// token is removed best-effort from every node. A partial renewal leaves nodes with different
    /// expiries, so the previous validity estimate no longer holds and the lease cannot be kept.
    /// </remarks>
    public async ValueTask<bool> ResetExpiryAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        RespireRedlockNodes.ValidateDuration(duration, nameof(duration));
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReleased) return false;
            var started = _nodes.Clock.GetTimestamp();
            _nodes.ValidateDeadline(started, duration, nameof(duration));
            bool[] renewed;
            try
            {
                renewed = await _nodes.RunAsync(
                    (client, token) => client.Locks.ResetExpiryAsync(Key, Token, duration, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await LoseOwnershipAsync().ConfigureAwait(false);
                throw;
            }

            if (_nodes.TryCreateLease(renewed, duration, started) is { } lease)
            {
                Volatile.Write(ref _lease, lease);
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
    /// <param name="cancellationToken">
    /// Cancels only waiting for a concurrent renewal or release. Once started, the release runs to
    /// completion on every node, bounded by <see cref="RespireRedlockOptions.NodeTimeout"/>.
    /// </param>
    /// <remarks>The handle stops reporting ownership before any node is contacted.</remarks>
    public async ValueTask<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _releaseConfirmed) != 0) return false;
            Volatile.Write(ref _released, 1);
            var confirmed = await _nodes.ReleaseAsync(Key, Token).ConfigureAwait(false);
            if (confirmed) Volatile.Write(ref _releaseConfirmed, 1);
            return confirmed;
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
        Volatile.Write(ref _released, 1);
        if (await _nodes.ReleaseAsync(Key, Token).ConfigureAwait(false))
            Volatile.Write(ref _releaseConfirmed, 1);
    }
}
