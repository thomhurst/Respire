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
        return SendAsync(new CredentialRenewalAuthCommand(credentials), cancellationToken);
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
        try
        {
            // Bound even a provider that returns an incomplete task without honoring cancellation.
            // The provider remains caller-owned; cancellation must not dispose it.
            var pending = options.CredentialProvider!.GetCredentialsAsync(linked.Token);
            var credentials = pending.IsCompletedSuccessfully ? pending.Result
                : await pending.AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (credentials is null || credentials.ExpiresAt <= options.CredentialTimeProvider.GetUtcNow())
                throw new RespireAuthenticationException($"Credential provider returned null or expired credentials for {host}:{port}.");
            return credentials;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RespireAuthenticationException) { throw; }
        catch (Exception error)
        {
            throw new RespireAuthenticationException($"Credential acquisition failed for {host}:{port}.", error);
        }
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
                    if (current.ExpiresAt is not { } expiry) return;
                    var remaining = expiry - clock.GetUtcNow();
                    if (remaining <= TimeSpan.Zero)
                    {
                        Expire();
                        return;
                    }
                    var delay = retry ? options.CredentialRefreshRetryDelay : remaining - options.CredentialRefreshBeforeExpiry;
                    if (delay > TimeSpan.Zero)
                    {
                        delay = Min(delay, remaining, TimeSpan.FromDays(1));
                        await Task.Delay(delay, clock, _stop.Token).ConfigureAwait(false);
                        remaining = expiry - clock.GetUtcNow();
                        if (remaining <= TimeSpan.Zero)
                        {
                            Expire();
                            return;
                        }
                        if (!retry && remaining > options.CredentialRefreshBeforeExpiry) continue;
                    }

                    using var deadline = new CancellationTokenSource(Min(remaining, options.ConnectTimeout), clock);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, deadline.Token);
                    RespireCredentials next;
                    try
                    {
                        next = await AcquireCredentialsAsync(connection.Host, connection.Port, options, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (Exception)
                    {
                        Record(succeeded: false, "provider");
                        retry = true;
                        continue;
                    }

                    if (_stop.IsCancellationRequested) return;
                    if (clock.GetUtcNow() >= expiry)
                    {
                        Expire();
                        return;
                    }
                    if (next.ExpiresAt == current.ExpiresAt && next.Username == current.Username && next.Password == current.Password)
                    {
                        Record(succeeded: null, "unchanged");
                        retry = true;
                        continue;
                    }

                    var renewed = false;
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
                        using var authCancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, authDeadline.Token);
                        PublishCacheMetrics(InvalidateCache());
                        using var reply = await connection.SendCredentialRenewalAsync(next,
                            authCancellation.Token).ConfigureAwait(false);
                        if (reply.Type != RespDataType.SimpleString || !reply.AsSpan().SequenceEqual("OK"u8))
                            throw new RespireAuthenticationException($"Credential renewal was rejected by {connection.Host}:{connection.Port}.");
                        var completedAt = clock.GetUtcNow();
                        if (completedAt >= expiry || next.ExpiresAt <= completedAt)
                            throw new RespireAuthenticationException($"Credentials expired during renewal for {connection.Host}:{connection.Port}.");
                        current = next;
                        retry = false;
                        renewed = true;
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        connection.Abort(error as RespireAuthenticationException
                            ?? new RespireAuthenticationException($"Credential renewal failed for {connection.Host}:{connection.Port}.", error));
                        Record(succeeded: false, "reauthenticate");
                        return;
                    }
                    finally
                    {
                        var evictions = InvalidateCache();
                        // Failure/stop keeps admission fenced until Abort marks the socket dead.
                        // In particular, RequestStop can run before Abort acquires _writeGate.
                        if (renewed) connection.CompleteCredentialRenewal();
                        PublishCacheMetrics(evictions);
                    }
                    Record(succeeded: true, "reauthenticate");
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                Record(succeeded: false, "worker");
                connection.Abort(new RespireAuthenticationException($"Credential renewal stopped for {connection.Host}:{connection.Port}.", error));
            }
        }

        private int? InvalidateCache()
        {
            try { return options.CredentialCacheInvalidation?.Invoke(); }
            catch { return null; }
        }

        private static void PublishCacheMetrics(int? evictions)
        {
            // Publish only after successful admission resumes (or after failure aborts).
            // A metrics observer must not run while it could wait on our AUTH fence.
            if (evictions is not { } count) return;
            try { ClientSideCacheCoordinator.PublishContinuityFlushMetrics(count); }
            catch { /* Instrumentation cannot change authentication state. */ }
        }

        private void Expire()
        {
            Record(succeeded: false, "expired");
            connection.Abort(new RespireAuthenticationException($"Credentials expired before renewal for {connection.Host}:{connection.Port}."));
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
