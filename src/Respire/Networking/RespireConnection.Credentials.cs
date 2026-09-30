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
        try
        {
            // Bound even a provider that returns an incomplete task without honoring cancellation.
            // The provider remains caller-owned; cancellation must not dispose it.
            var pending = options.CredentialProvider!.GetCredentialsAsync(linked.Token);
            credentials = pending.IsCompletedSuccessfully ? pending.Result
                : await pending.AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // Provider-owned messages and inner exceptions can contain tokens, including
            // exceptions already typed as RespireAuthenticationException. Do not retain them.
            throw new RespireAuthenticationException($"Credential acquisition failed for {host}:{port}.");
        }
        if (credentials is null || credentials.ExpiresAt <= options.CredentialTimeProvider.GetUtcNow())
            throw new RespireAuthenticationException($"Credential provider returned null or expired credentials for {host}:{port}.");
        return credentials;
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
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
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
            while (!_stop.IsCancellationRequested && connection.IsAcceptingCommands)
            {
                var remaining = expiry - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    AbortExpired();
                    return false;
                }
                var delay = retry ? options.CredentialRefreshRetryDelay : remaining - options.CredentialRefreshBeforeExpiry;
                if (delay <= TimeSpan.Zero) return true;
                await Task.Delay(Min(delay, remaining, TimeSpan.FromDays(1)), clock, _stop.Token).ConfigureAwait(false);
                if (!connection.IsAcceptingCommands) return false;
                remaining = expiry - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    AbortExpired();
                    return false;
                }
                if (retry || remaining <= options.CredentialRefreshBeforeExpiry) return true;
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
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                Record(succeeded: false, "provider");
                return null;
            }
        }

        private async ValueTask<RenewalOutcome> ReauthenticateAsync(
            RespireCredentials current, RespireCredentials next, CancellationToken cancellationToken)
        {
            var renewed = false;
            var clock = options.CredentialTimeProvider;
            var expiry = current.ExpiresAt!.Value;
            try
            {
                if ((next.Username ?? "default") != (current.Username ?? "default"))
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
                renewed = true;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return RenewalOutcome.Stopped; }
            // Retirement can win before AUTH is enqueued. Accepted commands must still drain.
            catch (RespireConnectionRetiredException) { return RenewalOutcome.Stopped; }
            catch (Exception error)
            {
                connection.Abort(error as RespireAuthenticationException
                    ?? new RespireAuthenticationException($"Credential renewal failed for {connection.Host}:{connection.Port}.", error));
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                Record(succeeded: false, "reauthenticate");
                return RenewalOutcome.Failed;
            }
            finally
            {
                // AUTH replies resume inline on the serial completion worker. This method
                // owns the observers, so this yield also covers a synchronously completed send.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                var evictions = InvalidateCache();
                // Only validated success opens admission. Stop/failure leaves it closed until
                // Abort; RequestStop can run before Abort acquires the write gate.
                if (renewed)
                {
                    // The scheduler hop or cache mutation may outlast the credentials.
                    var completedAt = clock.GetUtcNow();
                    if (completedAt >= expiry || next.ExpiresAt <= completedAt)
                        throw new RespireAuthenticationException($"Credentials expired during renewal for {connection.Host}:{connection.Port}.");
                    connection.CompleteCredentialRenewal();
                }
                PublishCacheMetrics(evictions);
            }
            Record(succeeded: true, "reauthenticate");
            return RenewalOutcome.Renewed;
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

        private static TimeSpan Min(TimeSpan first, TimeSpan second)
            => first <= second ? first : second;

        private static TimeSpan Min(TimeSpan first, TimeSpan second, TimeSpan third)
            => Min(Min(first, second), third);
    }
}
