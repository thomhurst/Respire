---
title: Dependency injection
description: Register lazy Respire clients in ASP.NET Core and worker services.
---

# Dependency injection

`Respire.DependencyInjection` registers a lazy `IRespireClient` in ASP.NET Core and worker
services. Startup does not wait for Redis.

## Register a default client

Install the latest stable [Respire.DependencyInjection](https://www.nuget.org/packages/Respire.DependencyInjection) package. It brings in `Respire`:

```bash
dotnet add package Respire.DependencyInjection
```

To try a published `2.0.0-rc.N` candidate, add `--prerelease`, or pin the exact
version shown in its GitHub release. See [release candidates](../getting-started.md#release-candidates)
for the channel and version policy.

Register a connection string, then inject `IRespireClient`:

```csharp
builder.Services.AddRespire(
    builder.Configuration.GetConnectionString("redis")!);
```

<!-- doc-test-declaration -->
```csharp
public sealed class SessionStore(IRespireClient redis)
{
    public ValueTask<Session?> GetAsync(string id, CancellationToken ct) =>
        redis.GetAsync<Session>($"session:{id}", ct);
}
```

The container owns disposal.

## Named clients

Use keyed services for separate endpoints:

<!-- doc-test-tail-declaration: split-before=public sealed class CartService -->
```csharp
builder.Services.AddKeyedRespire("sessions", "redis://sessions-host");
builder.Services.AddKeyedRespire("jobs", "redis://jobs-host");

public sealed class CartService(
    [FromKeyedServices("sessions")] IRespireClient sessions)
{
    public ValueTask<string?> GetAsync(string id, CancellationToken ct) =>
        sessions.GetStringAsync($"cart:{id}", ct);
}
```

## Configure options

Use the options overload for timeouts, connection counts, serialization, or logging. Keep
credentials in configuration providers, not in source.

```csharp
builder.Services.AddRespire(options =>
{
    options.Endpoints.Add(new RespireEndpoint("redis.internal"));
    options.CommandTimeout = TimeSpan.FromSeconds(2);
    options.Connections = 2;
    options.UseClientSideCaching();
});
```

- To build options from other services, pass a `Func<IServiceProvider, RespireOptions>`.
  `AddKeyedRespire` has both forms too.
- Without `LoggerFactory`, the client uses the container's `ILoggerFactory`.
- Register the default client, and each key, once. A duplicate throws
  `InvalidOperationException` immediately.
- Several `Endpoints` require `UseCluster = true` or `SentinelPrimaryName`; otherwise the
  container throws `RespireConfigurationException` on first resolve. Endpoint order is not
  failover: register separate named clients for independent deployments.
- For Sentinel, set `SentinelPrimaryName` and the Sentinel endpoints. Discovery happens on the
  first network operation. To validate the primary at startup, use
  `await RespireClient.ConnectAsync(options)` instead. Consumers and prefixed views keep the
  same client when the primary changes.

The mutable builder is generated from `RespireOptions`: every public option is available,
with the same defaults, including replica routing, retry policies, circuit breakers, maintenance
notifications, and stream producer identity.

For shared web and worker clients, set `options.RunContinuationsAsynchronously = true`
when code after command awaits does substantial CPU work. It defaults to `false`, as
with direct clients. See [command continuations](../fundamentals/connections.md#command-continuations)
for dispatch costs, ordering, and `StalledDeliveryThreshold`.

## Bind appsettings.json and validate at startup

Pass a configuration section to register both a client and `IOptions<RespireOptions>`:

```json
{
  "Redis": {
    "Endpoints": ["localhost:6379"],
    "KeyPrefix": "app:",
    "CommandTimeout": "00:00:02",
    "Connections": 2,
    "RetryPolicy": { "MaxAttempts": 3, "MaxCommandRetryCategory": "ReadOnly" },
    "CircuitBreaker": { "MinimumFailureCount": 5 }
  }
}
```

```csharp
builder.Services.AddRespire(builder.Configuration.GetSection("Redis"));
```

Configuration registrations use `ValidateOnStart`. Starting the host checks endpoints,
timeouts, nested policies, and incompatible combinations using the same validation as the
client. Invalid settings throw `OptionsValidationException`, including the options name and
the invalid setting. Parsing errors also identify the configuration path. Startup validation
does not connect to Redis or acquire credentials. Outside a host, validation runs when options
or the client are first resolved.

`AddKeyedRespire("jobs", configurationSection)` also registers named options. Use
`IOptionsMonitor<RespireOptions>.Get("jobs")` or `IOptionsSnapshot<RespireOptions>.Get("jobs")`
to inspect them, and `[FromKeyedServices("jobs")]` to inject the client. Different names keep
their own configuration. The connection-string and action overloads retain their lazy
validation on client resolution.

All serializable options use their `RespireOptions` property names. Endpoint arrays accept
strings or `{ "Host": "localhost", "Port": 6379 }` objects. Prefixes are literal text, enums
use their names, and time spans use strings such as `"00:00:02"`. An empty string clears a
nullable timeout; an omitted setting keeps its default. Nested objects configure
`HedgedReads`, `ReconnectPolicy`, `RetryPolicy`, `CircuitBreaker`, and `ClientSideCache`.
TLS objects accept scalar settings such as `TargetHost`, `EnabledSslProtocols`, and
`CertificateRevocationCheckMode`.

Service instances (`CredentialProvider`, `SentinelCredentialProvider`, `Serializer`, and
`LoggerFactory`), TLS certificate objects and callbacks, and binary prefixes are supplied
through the optional callback, which runs after binding:

```csharp
builder.Services.AddRespire(builder.Configuration.GetSection("Redis"),
    (services, options) => options with
    {
        CredentialProvider = services.GetRequiredService<IRespireCredentialProvider>()
    });
```

The callback requires that the credential provider has already been registered. Keep secrets
in a configuration secret provider. `RespireOptions` is an immutable record: use this callback
and return an `options with { ... }` expression to change bound settings. Standard
`Configure<RespireOptions>` and `PostConfigure<RespireOptions>` callbacks still run, but cannot
assign init-only properties or replace the record. Register `IValidateOptions<RespireOptions>`
to add validation rules. A custom `IOptionsFactory<RespireOptions>` registered before
`AddRespire` takes precedence over Respire's configuration factory; do not combine the two
unless your factory supplies the configuration-bound options itself.

Options are read when first resolved; existing singleton
clients do not reconnect or change settings when configuration reloads. Restart the host to
apply client configuration changes.

For ASP.NET Core cache abstractions, continue to [caching integrations](./caching).
For readiness endpoints, see [health checks](./health-checks).

## Default key namespaces with Aspire

`Respire.Aspire` reads a key prefix from `Aspire:Respire:Options:KeyPrefix`, overridden per
client by `Aspire:Respire:<connectionName>:Options:KeyPrefix`. `AddRespireClient` and
`AddKeyedRespireClient` both apply it.

- Values are literal text, not percent-decoded.
- An absent value keeps the connection-string or global prefix; an empty value clears it.
- The `configureOptions` callback runs last and can set a binary prefix through
  `RespireOptions.KeyPrefix`.

See
[default root namespaces](../commands/strings-and-keys.md#default-root-namespace)
for key, scan, cache and disposal behavior.
