---
sidebar_position: 1
slug: /
title: Meet Respire
description: A fast, modern RESP client for .NET.
---

# Redis code that feels like .NET

Respire is a fast, modern client for Redis, Valkey, KeyDB, and other RESP servers. It keeps protocol details out of application code.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync("greeting", "hello", expiry: TimeSpan.FromMinutes(5));
string? greeting = await redis.GetStringAsync("greeting");
```

The public API may change before the first supported major release, targeted at
**2.0.0**, including APIs without an experimental opt-in. See the
[installation guide](./getting-started#release-candidates) for stable package and
release candidate selection, the [roadmap](./roadmap) for supported features, and the
[experimental API policy](./experimental-apis) for surfaces that require an explicit opt-in.

## Why Respire

- **Server-assisted client caching.** Hot reads come from bounded local memory; Redis
  invalidates entries when keys change.
- **Real return types.** Commands return `string?`, `long`, `bool`, `TimeSpan`, or `T?`.
- **Async-first.** Pub/sub, stream consumers, and key scanning use `IAsyncEnumerable`.
- **Discoverable commands.** Data types live behind facets such as `redis.Hashes` and `redis.Streams`.
- **Automatic pipelining.** Concurrent calls share socket writes with no batching switch.
- **Blocking commands.** `BLPOP` and blocking stream reads use dedicated connections.
- **Integrations.** OpenTelemetry and typed serialization are built in. Dependency injection, Microsoft caching abstractions, and more ship as [separate packages](./packages).

## Design bets

| Bet | Rejected alternative | Why |
| --- | --- | --- |
| Server-assisted client cache | Every read crosses the network, or every app builds its own cache | Redis tracking gives coherent hot reads without application invalidation plumbing |
| Facets per data type (`redis.Hashes.GetAsync`) | Hundreds of flat prefixed methods | IntelliSense as documentation; the facet is the namespace |
| Real return types, serializer for `T` | Protocol union struct (`RedisValue`-style) | Union types push protocol details and disposal onto every caller |
| Explicit lease API for zero-copy | Disposable results everywhere | A disposal obligation should be visible at the call site |
| Blocking commands on dedicated pooled connections | Forbidding them | The capability is why people use lists and streams as queues |
| Throwing server errors | Error-as-value inspection | One error model; `.Code` carries the Redis error class |
| Batch results are plain values, read after the batch is sent | Awaitable results that deadlock before flush | Only the batch is awaited, so the await-before-flush deadlock cannot be written |
| Cancellation abandons the wait | Cancelling partially written frames | The wire layer never corrupts the shared connection |

## A small, typed surface

String and key operations sit on the client. Other commands are grouped by data type:

```csharp
await redis.Hashes.SetAsync("user:1", "name", "Ada");
await redis.Lists.RightPushAsync("jobs", "resize:42");
await redis.SortedSets.AddAsync("leaderboard", "ada", 98.5);

await foreach (var key in redis.Keys.ScanAsync(match: "user:*"))
{
    Console.WriteLine(key);
}
```

## Interface compatibility

Before the first supported major release (2.0.0), public interfaces may change.
Command facets, script-cache operations,
cache-aside calls, invalidation observation and prefix views are required members: custom implementations and
decorators must implement or forward them. Missing implementations are compile errors;
there are no default members that report an unsupported capability by throwing
`NotSupportedException`. Server-version and configuration requirements still apply.

From 2.0 onward, adding required members to an existing public interface requires a
major release. Minor releases can add extension methods composed from existing
operations, or separate optional capability interfaces that callers check explicitly.
Default interface implementations are allowed only when they provide complete behavior
using the existing contract, such as forwarding a convenience overload. They must not
hide an unavailable feature behind a throwing default.

Physical name resolution is an integration capability, separate from `IRespireClient`.
`RespireClient` and its prefixed views implement `IRespireNameResolver`:

```csharp
if (redis is IRespireNameResolver resolver)
{
    RespireKey physicalKey = resolver.ResolveKey("item");
    RespireChannel physicalChannel = resolver.ResolveChannel("events");
}
```

Decorators used with integrations that need physical names (including cache coherence,
notification-driven coordination waits, streaming dead-letter validation and the
StackExchange.Redis adapter) must also implement `IRespireNameResolver` and forward both
methods to the wrapped client's resolver. Such integrations reject a missing resolver
with `RespireConfigurationException`; they never assume that a custom client has no
prefix. Command-only implementations need not expose physical names.

## Choose your next step

- [Get connected](./getting-started) and run the first command.
- Make hot reads local with [server-assisted client-side caching](./fundamentals/client-side-caching).
- Learn how [values and serialization](./fundamentals/values-and-serialization) work.
- Build a [blocking work queue](./guides/blocking-queues).
- Add Respire to an ASP.NET Core app with [dependency injection](./integrations/dependency-injection).
