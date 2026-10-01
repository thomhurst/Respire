---
title: Renewable credentials
description: Rotate Redis credentials on live connections with a caller-owned provider.
---

# Renewable credentials

Set `RespireOptions.CredentialProvider` to an `IRespireCredentialProvider`. It takes precedence
over static `Username` and `Password`. Every new physical connection, including reconnects,
Cluster nodes, dedicated blocking connections, and subscriptions, obtains current credentials.
Static credentials retain their existing behavior and create no refresh worker.

This example reads a password and its absolute expiry from reloadable configuration. Your
configuration source must update both values together before expiry. A provider can instead
call a token service asynchronously; cache and synchronize that acquisition inside the provider.

<!-- doc-test-tail-declaration: split-before=public sealed class ConfigurationCredentials -->
```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("redis.internal", 6380) },
    UseTls = true,
    Protocol = RespProtocol.Resp3,
    CredentialProvider = new ConfigurationCredentials(configuration),
    CredentialRefreshBeforeExpiry = TimeSpan.FromMinutes(5),
    CredentialRefreshRetryDelay = TimeSpan.FromSeconds(5),
});

public sealed class ConfigurationCredentials(
    Microsoft.Extensions.Configuration.IConfiguration configuration) : IRespireCredentialProvider
{
    public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var password = configuration["Redis:Password"]
            ?? throw new InvalidOperationException("Redis password is missing.");
        var expiry = DateTimeOffset.Parse(configuration["Redis:ExpiresAt"]!,
            System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.FromResult(new RespireCredentials(
            configuration["Redis:Username"], password, expiry));
    }
}
```

Use an ISO 8601 timestamp with a UTC offset for `Redis:ExpiresAt`. A null username selects
Redis's `default` user. A null `ExpiresAt` disables proactive renewal for that connection;
new connections still query the provider. See the [Azure Managed Redis authentication guide](azure-managed-redis.md)
for its Microsoft Entra credential adapter and the [AWS IAM credentials guide](aws-iam-credentials.md)
for the ElastiCache and MemoryDB adapters.

## Ownership and identity

The application owns the provider and keeps it alive until every client using it is disposed.
Respire never disposes the provider. Calls may overlap across physical connections; renewal and its fixed retry delay are per
connection. Large pools can therefore call a shared provider concurrently or retry together.
Cache and coalesce token acquisition inside the caller-owned provider when that load matters. Honor
cancellation, avoid blocking before returning a `ValueTask`, and keep cancellation callbacks
short. Client disposal cancels outstanding acquisition and joins its refresh workers.

Return the same ACL user across all calls for a client's lifetime. Live renewal rejects a
changed username; use a new client to change identity. A provider is responsible for returning
a consistent identity across new connections too. `RespireCredentials.ToString()` redacts
credentials. Do not log its `Password` property, provider exceptions containing tokens, or
raw configuration.

## Renewal and failure behavior

Each expiring connection starts renewal at `ExpiresAt - CredentialRefreshBeforeExpiry`.
The lead time and retry delay must each be at least one millisecond. Acquisition is bounded
by `ConnectTimeout`; renewal is also bounded by the current credentials' remaining lifetime.
Null or already expired credentials fail acquisition with `RespireAuthenticationException`.
Initial AUTH/HELLO rejection and failed live re-authentication also expose this exception type.
Provider-backed AUTH/HELLO errors are redacted regardless of the server error code. Exceptions
thrown during provider acquisition are replaced with a generic typed failure without preserving
provider text or an inner exception, including provider-thrown authentication exceptions.
Record any safe provider diagnostics inside your own provider; Respire cannot determine which
parts of an arbitrary exception contain credentials. Caller cancellation retains the caller's token.

If acquisition fails while existing credentials remain valid, the connection stays open and
retries after `CredentialRefreshRetryDelay`. Returning the same username, password, and expiry
also schedules a retry rather than spinning. At expiry, or if Redis rejects AUTH, the connection
closes. Its normal reconnect policy determines replacement attempts, which acquire credentials
again. A provider failure does not silently stop renewal. A successful replacement without an
expiry ends proactive renewal on that connection.

AUTH uses the ordinary connection FIFO and write gate. In-flight replies retain their order,
and AUTH cannot split an atomic MULTI/EXEC frame. While replacement AUTH is pending, new
application commands wait outside the wire queue, including transactions and fire-and-forget
writes. Admission resumes only after Redis returns OK and the replacement remains unexpired;
rejection, expiry, or disposal aborts the socket without sending those waiting commands.
Waiting callers retain their cancellation and command-timeout bounds. Client-side cache state is flushed before and
after re-authentication, preventing replies from an earlier cache epoch from being retained.
If that internal cache mutation fails, the connection aborts without reopening command admission.
This differs from an optional metrics callback failure, which remains isolated.

A server-blocking command such as BLPOP prevents Redis from processing a later AUTH until the
command completes. Keep blocking durations and token acquisition latency within the renewal
window. AUTH must finish before either the old or replacement credentials expire, and within
the connection timeout remaining for that attempt. An indefinite block can therefore fail
when renewal reaches its deadline. The refresh worker does not replay that command.

## Pub/Sub and Sentinel

Use `Protocol = RespProtocol.Resp3` for provider-backed subscriptions. Redis allows AUTH while
subscribed in RESP3, preserving the existing socket and subscriptions. Respire rejects
provider-backed subscriptions using `Resp2` or `Auto`: automatic fallback could select RESP2, and Redis [forbids AUTH in that subscribed state](https://redis.io/docs/latest/commands/subscribe/).
It does not silently change an explicit protocol selection or promise lossless reconnects.

`SentinelCredentialProvider` supplies independent discovery credentials. When it is null,
explicit `SentinelUsername` or `SentinelPassword` uses static Sentinel authentication instead
of inheriting the data provider. With neither override, discovery inherits `CredentialProvider`.
An empty `SentinelPassword` explicitly disables Sentinel authentication, including providers.
These rules keep discovery and data identities separate.

## Diagnostics and dependency injection

The `Respire` meter publishes `respire.authentication.refresh`, tagged with `server.address`,
`server.port`, `respire.authentication.stage` (`provider`, `reauthenticate`, `unchanged`, `no-expiry`, `expired`, or `worker`),
and `respire.authentication.outcome` (`success`, `failure`, or `retry`). Successful AUTH records success;
failed acquisition, rejected/unfinished AUTH, expiry, and unexpected worker failure record
failure. Unchanged credentials record stage `unchanged` and outcome `retry`, without a failure
warning or an AUTH attempt. A replacement without expiry records `no-expiry` with outcome
`success` after the successful AUTH event, indicating that proactive renewal has stopped. Warnings use
`CredentialRefreshFailed` (event ID 4001). Neither telemetry nor these warnings includes
credential values or provider exception text. Exceptions thrown by listeners or loggers do
not terminate renewal; callbacks must still return promptly.

The dependency-injection `RespireOptionsBuilder` exposes `CredentialProvider`,
`SentinelCredentialProvider`, `CredentialRefreshBeforeExpiry`, and `CredentialRefreshRetryDelay`.
Assign an application-owned provider when configuring `AddRespire`. Ensure its lifetime covers
the registered client; Respire does not assume disposal ownership when passed through options.
