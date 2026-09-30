using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private CredentialSession? _credentialSession;

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
                        retry = true;
                        continue;
                    }

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
                        InvalidateCache();
                        using var reply = await connection.SendAsync(new AuthCommand(next.Username, next.Password),
                            authCancellation.Token).ConfigureAwait(false);
                        if (reply.Type != RespDataType.SimpleString || !reply.AsSpan().SequenceEqual("OK"u8))
                            throw new RespireAuthenticationException($"Credential renewal was rejected by {connection.Host}:{connection.Port}.");
                        var completedAt = clock.GetUtcNow();
                        if (completedAt >= expiry || next.ExpiresAt <= completedAt)
                            throw new RespireAuthenticationException($"Credentials expired during renewal for {connection.Host}:{connection.Port}.");
                        current = next;
                        retry = false;
                        Record(succeeded: true, "reauthenticate");
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        Record(succeeded: false, "reauthenticate");
                        connection.Abort(error as RespireAuthenticationException
                            ?? new RespireAuthenticationException($"Credential renewal failed for {connection.Host}:{connection.Port}.", error));
                        return;
                    }
                    finally { InvalidateCache(); }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                Record(succeeded: false, "worker");
                connection.Abort(new RespireAuthenticationException($"Credential renewal stopped for {connection.Host}:{connection.Port}.", error));
            }
        }

        private void InvalidateCache()
        {
            // Cache mutation precedes its optional metrics callbacks. A listener failure
            // must not turn successful authentication into a broken transport.
            try { options.CredentialCacheInvalidation?.Invoke(); }
            catch { }
        }

        private void Expire()
        {
            Record(succeeded: false, "expired");
            connection.Abort(new RespireAuthenticationException($"Credentials expired before renewal for {connection.Host}:{connection.Port}."));
        }

        private void Record(bool succeeded, string stage)
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
