---
title: Renewable credentials
description: Rotate Redis credentials on live connections with a caller-owned provider.
---

# Renewable credentials

Set `RespireOptions.CredentialProvider` to an `IRespireCredentialProvider` to rotate credentials
on live connections. It overrides static `Username` and `Password`. Every new connection (reconnects,
Cluster nodes, dedicated blocking connections, subscriptions) asks it for current credentials.

This example reads a password and its absolute expiry from reloadable configuration, which must
update both values together before expiry. A provider can also call a token service; cache and
synchronize that call inside the provider.

<!-- doc-test-tail-declaration: split-before=public sealed class ConfigurationCredentials -->
```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("redis.internal", 6380) },
    UseTls = true,
    Protocol = RespireProtocol.Resp3,
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

- Use an ISO 8601 timestamp with a UTC offset for `Redis:ExpiresAt`.
- A null username selects Redis's `default` user.
- A null `ExpiresAt` disables proactive renewal for that connection; new connections still query the provider.

| Option | Default | Meaning |
| --- | --- | --- |
| `CredentialRefreshBeforeExpiry` | 5 minutes | Renewal starts at `ExpiresAt` minus this. At least 1 ms. |
| `CredentialRefreshRetryDelay` | 5 seconds | Delay before retrying a failed or unchanged renewal. At least 1 ms. |

For ready-made providers, see [Azure Managed Redis](azure-managed-redis.md) (Microsoft Entra),
[AWS IAM credentials](aws-iam-credentials.md) (ElastiCache and MemoryDB), and
[Google Cloud Memorystore](google-cloud-memorystore.md) (Redis Cluster and Valkey).

## Ownership and identity

- The application owns the provider and keeps it alive until every client using it is disposed. Respire never disposes it.
- Calls can overlap across connections, and renewal and retries are per connection, so large pools can call the provider concurrently. Cache and coalesce token acquisition inside the provider if that load matters.
- Honor cancellation, avoid blocking before returning the `ValueTask`, and keep cancellation callbacks short.
- Client disposal cancels outstanding acquisition and stops refresh workers.
- Return the same ACL user for the client's lifetime. Live renewal rejects a changed username; use a new client to change identity.

:::warning
`RespireCredentials.ToString()` redacts credentials. Do not log its `Password` property, provider
exceptions that contain tokens, or raw configuration.
:::

## Renewal and failure behavior

- Acquisition is bounded by `ConnectTimeout`, and renewal also by the current credentials' remaining lifetime.
- Null or expired credentials fail with `RespireAuthenticationException`. So do initial `AUTH`/`HELLO` rejection and failed re-authentication.
- Provider-backed `AUTH`/`HELLO` errors are redacted. Exceptions thrown by the provider are replaced with a generic failure with no provider text or inner exception. Log safe diagnostics inside your provider. Caller cancellation keeps the caller's token.
- If acquisition fails while current credentials are still valid, the connection stays open and retries after `CredentialRefreshRetryDelay`. Returning identical credentials also schedules a retry.
- At expiry, or if Redis rejects `AUTH`, the connection closes. Its reconnect policy handles replacement, which acquires credentials again.
- A successful renewal without an expiry ends proactive renewal on that connection.

During re-authentication, in-flight replies keep their order and `AUTH` never splits a
`MULTI`/`EXEC` frame. New commands, including transactions and fire-and-forget writes, wait until
Redis returns OK. If `AUTH` is rejected, credentials expire or the client is disposed, the socket
closes without sending them. Waiting callers keep their cancellation and command timeouts. The
client-side cache is flushed before and after re-authentication.

:::warning
A blocking command such as `BLPOP` stops Redis from processing a later `AUTH` until it completes.
`AUTH` must finish before the old or new credentials expire and within the connection timeout.
Keep blocking durations and token latency inside the renewal window, or renewal fails. The blocked
command is not replayed.
:::

## Pub/Sub and Sentinel

Use `Protocol = RespireProtocol.Resp3` for provider-backed subscriptions. RESP3 allows `AUTH` while
subscribed, keeping the socket and subscriptions. Respire rejects provider-backed subscriptions on
`Resp2` or `Auto`, because RESP2 [forbids AUTH while subscribed](https://redis.io/docs/latest/commands/subscribe/).

Sentinel discovery credentials:

| Configuration | Discovery uses |
| --- | --- |
| `SentinelCredentialProvider` set | That provider |
| `SentinelUsername` or `SentinelPassword` set | Static Sentinel credentials |
| Neither | `CredentialProvider` |
| Empty `SentinelPassword` | No authentication, even with providers |

## Diagnostics and dependency injection

The `Respire` meter publishes `respire.authentication.refresh` with tags `server.address`,
`server.port`, `respire.authentication.stage` and `respire.authentication.outcome`:

| Event | Stage | Outcome |
| --- | --- | --- |
| Successful `AUTH` | `reauthenticate` | `success` |
| Failed acquisition, rejected or unfinished `AUTH`, expiry, worker failure | `provider`, `reauthenticate`, `expired` or `worker` | `failure` |
| Unchanged credentials (no `AUTH` sent, no warning) | `unchanged` | `retry` |
| Renewal without expiry; proactive renewal stops | `no-expiry` | `success` |

Warnings use `CredentialRefreshFailed` (event ID 4001). Telemetry and warnings never include
credential values or provider exception text. Throwing listeners or loggers do not stop renewal,
but callbacks must return promptly.

The dependency-injection `RespireOptionsBuilder` exposes `CredentialProvider`,
`SentinelCredentialProvider`, `CredentialRefreshBeforeExpiry` and `CredentialRefreshRetryDelay`.
Assign an application-owned provider in `AddRespire`; Respire does not take disposal ownership.
