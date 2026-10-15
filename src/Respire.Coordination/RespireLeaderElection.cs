using Microsoft.Extensions.Hosting;

namespace Respire.Coordination;

/// <summary>A hosted, renewable leader election backed by a fenced Redis lock.</summary>
/// <remarks>
/// Register this instance as an IHostedService or call StartAsync/StopAsync yourself.
/// All contenders must use the same distinct key pair in one Cluster slot. Preserve the counter:
/// Redis rollback or asynchronous failover can roll it back. Downstream resources must enforce fencing.
/// Cancellation is cooperative; a paused process can resume after its lease expires.
/// The client is caller-owned. Managed renewal requires CLIENT ID and CLIENT KILL permissions.
/// </remarks>
public sealed class RespireLeaderElection : BackgroundService
{
    private readonly RespireCoordination _coordination;
    private readonly RespireKey _key;
    private readonly RespireKey _counter;
    private readonly RespireLeaderElectionOptions _options;
    private readonly TimeProvider _clock;
    private RespireLeadership? _current;

    /// <summary>Creates an election without taking ownership of the client.</summary>
    public RespireLeaderElection(IRespireClient client, RespireKey key, RespireKey fencingCounterKey,
        RespireLeaderElectionOptions options)
        : this(client, key, fencingCounterKey, options, TimeProvider.System) { }

    internal RespireLeaderElection(IRespireClient client, RespireKey key, RespireKey fencingCounterKey,
        RespireLeaderElectionOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.OnElected);
        _ = RespireCoordination.ValidateAcquisition(key, fencingCounterKey, options.LeaseDuration);
        if (options.LeaseDuration > TimeSpan.FromMilliseconds(uint.MaxValue - 1d))
            throw new ArgumentOutOfRangeException(nameof(options), "Lease duration exceeds the timer limit.");
        if (options.RetryDelay <= TimeSpan.Zero || options.RetryDelay > options.LeaseDuration)
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delay must be positive and no longer than the lease.");
        _coordination = new(client, clock);
        _key = key.Snapshot();
        _counter = fencingCounterKey.Snapshot();
        _options = options;
        _clock = clock;
    }

    /// <summary>The current local term, or null after revocation. Always observe its cancellation token.</summary>
    public RespireLeadership? CurrentLeadership
    {
        get
        {
            var term = Volatile.Read(ref _current);
            return term is not null && !term.CancellationToken.IsCancellationRequested ? term : null;
        }
    }

    /// <summary>Reports election and revocation. Observer exceptions are isolated from lease ownership.</summary>
    public event EventHandler<RespireLeadershipChangedEventArgs>? LeadershipChanged;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            RespireFencedLockAttempt attempt;
            try
            {
                using var deadline = new CancellationTokenSource(_options.LeaseDuration, _clock);
                using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
                attempt = await _coordination.TryAcquireFencedLockAsync(
                    _key, _counter, _options.LeaseDuration, acquisition.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is RespireConnectionException or RespireTimeoutException or OperationCanceledException)
            {
                LeaderElectionMetrics.FailedAttempt();
                await Task.Delay(_options.RetryDelay, _clock, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await using (attempt.ConfigureAwait(false))
            {
                if (attempt.Acquired && !stoppingToken.IsCancellationRequested)
                    await RunTermAsync(attempt.Lock, stoppingToken).ConfigureAwait(false);
            }
            await Task.Delay(_options.RetryDelay, _clock, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunTermAsync(RespireFencedLock lease, CancellationToken stoppingToken)
    {
        using var termStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        RespireLockKeepAlive keepAlive;
        try { keepAlive = await lease.KeepAliveAsync(termStop.Token).ConfigureAwait(false); }
        catch (InvalidOperationException) when (lease.IsReleased) { return; }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        await using var renewal = keepAlive;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, renewal.CancellationToken);
        if (lifetime.IsCancellationRequested || lease.IsReleased) return;
        var term = new RespireLeadership(lease.FencingToken, lifetime.Token);
        Volatile.Write(ref _current, term);
        Publish(term, true);
        // Run user code independently so even its synchronous prefix cannot prevent revocation.
        var work = Task.Run(() => _options.OnElected(term), CancellationToken.None);
        var revoked = Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        try
        {
            await Task.WhenAny(work, revoked).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                try { await termStop.CancelAsync().ConfigureAwait(false); }
                finally { await lifetime.CancelAsync().ConfigureAwait(false); }
            }
            finally
            {
                Volatile.Write(ref _current, null);
                Publish(term, false);
                try
                {
                    if (_options.OnRevoked is { } onRevoked)
                        await onRevoked(term).ConfigureAwait(false);
                }
                finally
                {
                    // Never start another term while this instance's previous work is still running.
                    try { await work.ConfigureAwait(false); }
                    catch (OperationCanceledException error) when (
                        error.CancellationToken == term.CancellationToken && term.CancellationToken.IsCancellationRequested) { }
                }
            }
        }
    }

    private void Publish(RespireLeadership term, bool elected)
    {
        LeaderElectionMetrics.Changed(elected);
        var handlers = LeadershipChanged;
        if (handlers is null) return;
        var args = new RespireLeadershipChangedEventArgs(term, elected);
        foreach (EventHandler<RespireLeadershipChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception) { /* Observers cannot change an ownership decision. */ }
        }
    }
}
