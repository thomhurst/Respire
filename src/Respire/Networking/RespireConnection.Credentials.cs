using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private CredentialSession? _credentialSession;
    // Protected by _writeGate. Only the private renewal command can cross this fence.
    private bool _credentialRenewalPending;

    private readonly struct CredentialRenewalAuthCommand(RespireCredentials credentials) : IRespCommand
    {
        public void Write(ref RespWriter writer)
            => new AuthCommand(credentials.Username, credentials.Password).Write(ref writer);
    }

    private ValueTask<RespValue> SendCredentialRenewalAsync(RespireCredentials credentials, CancellationToken cancellationToken)
    {
        lock (_writeGate)
        {
            ThrowIfRetired();
            if (_dead) throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
            _credentialRenewalPending = true;
        }
        // The renewal's token already carries its connection/credential deadline.
        return SendAsync(new CredentialRenewalAuthCommand(credentials), cancellationToken, armCommandDeadline: false);
    }

    private void CompleteCredentialRenewal()
    {
        lock (_writeGate) _credentialRenewalPending = false;
        _capacitySignal.Signal();
    }

    internal Task? CredentialRefreshCompletion => _credentialSession?.Completion;

    private static async ValueTask<RespireConnectionOptions> ResolveCredentialsAsync(
        string host, int port, RespireConnectionOptions options, CancellationToken cancellationToken)
    {
        if (options.CredentialProvider is null) return options;
        var credentials = await AcquireCredentialsAsync(host, port, options, cancellationToken).ConfigureAwait(false);
        return options with
        {
            Username = credentials.Username, Password = credentials.Password, InitialCredentials = credentials,
        };
    }

    private static async ValueTask<RespireCredentials> AcquireCredentialsAsync(
        string host, int port, RespireConnectionOptions options, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.ConnectTimeout, options.CredentialTimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        RespireCredentials credentials;
        Task<RespireCredentials>? providerTask = null;
        try
        {
            // Bound even a provider that returns an incomplete task without honoring cancellation.
            // The provider remains caller-owned; cancellation must not dispose it.
            var pending = options.CredentialProvider!.GetCredentialsAsync(linked.Token);
            credentials = pending.IsCompletedSuccessfully ? pending.Result
                : await (providerTask = pending.AsTask()).WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveProviderFailure(providerTask);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            ObserveProviderFailure(providerTask);
            // Provider-owned messages and inner exceptions can contain tokens, including
            // exceptions already typed as RespireAuthenticationException. Do not retain them.
            throw new RespireAuthenticationException($"Credential acquisition failed for {host}:{port}.");
        }
        if (credentials is null || credentials.ExpiresAt <= options.CredentialTimeProvider.GetUtcNow())
            throw new RespireAuthenticationException($"Credential provider returned null or expired credentials for {host}:{port}.");
        return credentials;
    }

    private static void ObserveProviderFailure(Task<RespireCredentials>? providerTask)
    {
        if (providerTask is null) return;
        _ = providerTask.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StartCredentialRefresh(RespireConnectionOptions options)
    {
        if (options.InitialCredentials is not { ExpiresAt: not null } credentials) return;
        if (credentials.ExpiresAt <= options.CredentialTimeProvider.GetUtcNow())
            throw new RespireAuthenticationException($"Credentials expired during connection setup for {Host}:{Port}.");
        var session = new CredentialSession(this,
            options with { Username = null, Password = null, InitialCredentials = null }, credentials);
        _credentialSession = session;
        session.Start();
        if (!IsConnected) session.RequestStop();
    }

    private sealed class CredentialSession(
        RespireConnection connection, RespireConnectionOptions options, RespireCredentials initial) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _cancelTask;
        private bool _disposed;
        private RespireCredentials? _initial = initial;

        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Start() => Completion = Task.Run(RunAsync);

        public void RequestStop()
        {
            lock (_gate)
            {
                if (_disposed || _cancelTask is not null) return;
                // Provider cancellation callbacks must never run on the socket receive loop.
                _cancelTask = _stop.CancelAsync();
            }
        }

        private enum RenewalOutcome { Renewed, Stopped, Failed }

        private async Task RunAsync()
        {
            var current = _initial!;
            _initial = null;
            var retry = false;
            var clock = options.CredentialTimeProvider;
            try
            {
                while (!_stop.IsCancellationRequested && connection.IsAcceptingCommands)
                {
                    if (!await WaitUntilDueAsync(current, retry).ConfigureAwait(false)) return;
                    var expiry = current.ExpiresAt!.Value;
                    using var deadline = new CancellationTokenSource(Min(expiry - clock.GetUtcNow(), options.ConnectTimeout), clock);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, deadline.Token);
                    var next = await AcquireNextAsync(linked.Token).ConfigureAwait(false);
                    if (next is null)
                    {
                        retry = true;
                        continue;
                    }

                    // A provider or injected clock can complete inline on this connection's
                    // serial reply worker. Yield in this observer-owning state machine, not
                    // only in a helper whose task might complete before our await begins.
                    await YieldOffReplyWorker();
                    if (_stop.IsCancellationRequested || !connection.IsAcceptingCommands) return;
                    if (clock.GetUtcNow() >= expiry)
                    {
                        AbortExpired();
                        return;
                    }
                    if (next.IsSameAs(current))
                    {
                        Record(succeeded: null, "unchanged");
                        retry = true;
                        continue;
                    }
                    if (await ReauthenticateAsync(current, next, linked.Token).ConfigureAwait(false) != RenewalOutcome.Renewed)
                        return;
                    current = next;
                    retry = false;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                connection.Abort(new RespireAuthenticationException($"Credential renewal stopped for {connection.Host}:{connection.Port}.", error));
                Record(succeeded: false, "worker");
            }
        }

        private async ValueTask<bool> WaitUntilDueAsync(RespireCredentials current, bool retry)
        {
            var clock = options.CredentialTimeProvider;
            if (current.ExpiresAt is not { } expiry)
            {
                Record(succeeded: true, "no-expiry");
                return false;
            }
            // A retry consumes the whole configured delay; a refresh waits for the lead time.
            // Timers are armed in bounded chunks, and expiry is rechecked after every chunk.
            var retryDelay = options.CredentialRefreshRetryDelay;
            while (!_stop.IsCancellationRequested && connection.IsAcceptingCommands)
            {
                var remaining = expiry - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    AbortExpired();
                    return false;
                }
                var wait = retry ? retryDelay : remaining - options.CredentialRefreshBeforeExpiry;
                if (wait <= TimeSpan.Zero) return true;
                var delay = Min(wait, remaining, MaxTimerChunk);
                await Task.Delay(delay, clock, _stop.Token).ConfigureAwait(false);
                retryDelay -= delay;
            }
            return false;
        }

        private async ValueTask<RespireCredentials?> AcquireNextAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await AcquireCredentialsAsync(connection.Host, connection.Port, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                await YieldOffReplyWorker();
                Record(succeeded: false, "provider");
                return null;
            }
        }

        private async ValueTask<RenewalOutcome> ReauthenticateAsync(
            RespireCredentials current, RespireCredentials next, CancellationToken cancellationToken)
        {
            var clock = options.CredentialTimeProvider;
            var expiry = current.ExpiresAt!.Value;
            RenewalOutcome outcome;
            try
            {
                // Redis ACL user names are case-sensitive, so the comparison is ordinal.
                if (!string.Equals(next.Username ?? "default", current.Username ?? "default", StringComparison.Ordinal))
                    throw new RespireAuthenticationException($"Credential renewal changed the ACL user for {connection.Host}:{connection.Port}.");
                var authRemaining = expiry - clock.GetUtcNow();
                if (next.ExpiresAt is { } nextExpiry)
                    authRemaining = Min(authRemaining, nextExpiry - clock.GetUtcNow());
                if (authRemaining <= TimeSpan.Zero)
                    throw new RespireAuthenticationException($"Replacement credentials expired before renewal for {connection.Host}:{connection.Port}.");
                using var authDeadline = new CancellationTokenSource(Min(authRemaining, options.ConnectTimeout), clock);
                using var authCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authDeadline.Token);
                PublishCacheMetrics(InvalidateCache());
                using var reply = await connection.SendCredentialRenewalAsync(next, authCancellation.Token).ConfigureAwait(false);
                if (reply.Type != RespDataType.SimpleString || !reply.AsSpan().SequenceEqual("OK"u8))
                    throw new RespireAuthenticationException($"Credential renewal was rejected by {connection.Host}:{connection.Port}.");
                outcome = RenewalOutcome.Renewed;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { outcome = RenewalOutcome.Stopped; }
            // Retirement can win before AUTH is enqueued. Accepted commands must still drain.
            catch (RespireConnectionRetiredException) { outcome = RenewalOutcome.Stopped; }
            catch (Exception error)
            {
                // Abort before yielding so fenced callers observe the authentication failure promptly.
                connection.Abort(error as RespireAuthenticationException
                    ?? new RespireAuthenticationException($"Credential renewal failed for {connection.Host}:{connection.Port}.", error));
                outcome = RenewalOutcome.Failed;
            }

            // AUTH replies resume inline on the serial completion worker. This method owns
            // the observers, so this hop also covers a synchronously completed send.
            await YieldOffReplyWorker();
            if (outcome == RenewalOutcome.Failed) Record(succeeded: false, "reauthenticate");
            int? evictions = null;
            try
            {
                evictions = InvalidateCache();
            }
            catch (RespireAuthenticationException error)
            {
                // After a failure the connection is already aborted; keep the original error.
                if (outcome != RenewalOutcome.Failed)
                {
                    connection.Abort(error);
                    Record(succeeded: false, "reauthenticate");
                    outcome = RenewalOutcome.Failed;
                }
            }
            // Only validated success opens admission. Stop/failure leaves it closed until
            // Abort; RequestStop can run before Abort acquires the write gate.
            if (outcome == RenewalOutcome.Renewed)
            {
                // The scheduler hop or cache mutation may outlast the credentials.
                var completedAt = clock.GetUtcNow();
                if (completedAt >= expiry || next.ExpiresAt <= completedAt)
                {
                    connection.Abort(new RespireAuthenticationException($"Credentials expired during renewal for {connection.Host}:{connection.Port}."));
                    Record(succeeded: false, "expired");
                    outcome = RenewalOutcome.Failed;
                }
                else
                {
                    connection.CompleteCredentialRenewal();
                    Record(succeeded: true, "reauthenticate");
                }
            }
            PublishCacheMetrics(evictions);
            return outcome;
        }

        private int? InvalidateCache()
        {
            // This internal callback mutates cache state, not user instrumentation. Failure
            // must reach the worker's abort path; only metric observers may be ignored.
            try { return options.CredentialCacheInvalidation?.Invoke(); }
            catch (Exception error)
            {
                throw new RespireAuthenticationException($"Credential cache invalidation failed for {connection.Host}:{connection.Port}.", error);
            }
        }

        private static void PublishCacheMetrics(int? evictions)
        {
            // Publish only after successful admission resumes (or after failure aborts).
            // A metrics observer must not run while it could wait on our AUTH fence.
            if (evictions is not { } count) return;
            try { ClientSideCacheCoordinator.PublishContinuityFlushMetrics(count); }
            catch { /* Instrumentation cannot change authentication state. */ }
        }

        private void AbortExpired()
        {
            connection.Abort(new RespireAuthenticationException($"Credentials expired before renewal for {connection.Host}:{connection.Port}."));
            Record(succeeded: false, "expired");
        }

        private void Record(bool? succeeded, string stage)
            => RespireTelemetry.RecordCredentialRefresh(connection.Host, connection.Port, succeeded, stage, connection._logger);

        public async ValueTask DisposeAsync()
        {
            RequestStop();
            await Completion.ConfigureAwait(false);
            Task? cancellation;
            lock (_gate) cancellation = _cancelTask;
            if (cancellation is not null)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch { /* A provider's cancellation callback cannot prevent transport cleanup. */ }
            }
            lock (_gate)
            {
                _disposed = true;
                _stop.Dispose();
            }
        }

        // Observer callbacks (telemetry, cache invalidation and cache metrics) must never run
        // inline on the connection's serial reply worker: one that waits on this connection
        // would block the worker that has to deliver its reply. Each observer-owning state
        // machine awaits this after any await that can resume on that worker.
        private static ConfiguredTaskAwaitable YieldOffReplyWorker()
            => Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        // Bounds each armed timer; long waits are consumed in chunks with expiry rechecks.
        private static readonly TimeSpan MaxTimerChunk = TimeSpan.FromDays(1);

        private static TimeSpan Min(TimeSpan first, TimeSpan second)
            => first <= second ? first : second;

        private static TimeSpan Min(TimeSpan first, TimeSpan second, TimeSpan third)
            => Min(Min(first, second), third);
    }
}
