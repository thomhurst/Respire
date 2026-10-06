---
title: FusionCache
description: Share a Respire client between FusionCache's distributed cache and backplane.
---

# FusionCache

`Respire.FusionCache` provides an `IFusionCacheBackplane` for FusionCache 2.9.0 or later
compatible releases. `Respire.Caching` supplies its `IDistributedCache` L2. Both adapters
can share the same caller-owned `IRespireClient`.

```bash
dotnet add package Respire.FusionCache
dotnet add package Respire.Caching
dotnet add package ZiggyCreatures.FusionCache.Serialization.SystemTextJson
```

## Shared-client setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Respire.Caching;
using Respire.FusionCache;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

await using var client = await RespireClient.ConnectAsync("redis://localhost");
var services = new ServiceCollection();
services.AddSingleton<IRespireClient>(client);
services.AddRespireDistributedCache(options => options.InstanceName = "myapp:cache:");
services.AddFusionCache()
    .WithOptions(options =>
    {
        options.BackplaneChannelPrefix = "myapp:cache";
        options.WaitForInitialBackplaneSubscribe = true;
    })
    .WithSerializer(new FusionCacheSystemTextJsonSerializer())
    .WithRegisteredDistributedCache()
    .WithRespireBackplane();

await using var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<IFusionCache>();
await cache.SetAsync("product:42", 42);
```

The service provider disposes the cache and its backplane before the caller disposes
`client`. Neither adapter disposes that externally supplied client. The backplane uses
Respire's shared subscription infrastructure; it does not create another command client.
The serializer above is supplied by FusionCache. Configure its serialization separately
from Respire's serializer, including any application-specific AOT requirements.

For direct construction, pass a `RespireFusionCacheBackplane` to FusionCache's
`SetupBackplane`. For registered-service discovery, call `AddFusionCacheRespireBackplane`
and FusionCache's `WithRegisteredBackplane`. The registration is transient: each cache
needs a separate backplane instance, even when the client is shared. `WithRespireBackplane`
also creates a separate instance for each cache.

## Channels and compatibility

The adapter uses regular `SUBSCRIBE`/`PUBLISH` with the literal channel name selected by
FusionCache. It uses FusionCache's public `BackplaneMessage` serialization and interoperates
with `ZiggyCreatures.FusionCache.Backplane.StackExchangeRedis` 2.9.0. It does not opt into
sharded pub/sub. RESP2 and RESP3 are supported through the client's existing subscription
infrastructure.

Give communicating nodes the same cache name, channel prefix, L2 key prefix, and serializer.
Use distinct names/prefixes for unrelated applications. Redis pub/sub is not scoped to a
database, and Respire's `WithKeyPrefix` does not prefix these channels. A database number or
L2 key prefix alone therefore does not isolate backplane traffic.

## Delivery gaps and lifecycle

Respire reconnects and resubscribes automatically. The adapter consumes its ordered gap
markers outside the receive path, logs reconnect or buffer-overflow gaps, and invokes
FusionCache's connection handler with `IsReconnection = true`. Initial subscription reports
`false`. FusionCache applies its own recovery policy; Redis pub/sub cannot replay messages
lost during an outage or local overflow. The backplane does not promise durable delivery
or a database snapshot. The core pub/sub metrics continue to record these gaps.

Optional `RespireSubscriptionOptions` on the constructor and registration methods control
buffer size and overflow policy. Unspecified values inherit the client settings. Callbacks
run in order on a background consumer. Slow callbacks can fill the bounded buffer; malformed
messages and callback exceptions are logged without preventing later valid messages.
Synchronous and asynchronous subscription entry points use their corresponding FusionCache
callbacks, with the other callback form as a fallback when only that form is supplied.

Await successful unsubscribe before reusing an instance. Subscribing an active or still
retiring instance fails. A callback that unsubscribes itself must return before that instance
can be subscribed again.
Unsubscribe and disposal stop its transport subscription and join in-progress callbacks;
a callback that unsubscribes itself does not wait for itself. Disposal cannot interrupt
arbitrary application callback code, so callbacks must finish for external teardown to
complete. Concurrent teardown callers join the same work. Disposing the shared client or
exhausting its reconnect policy ends the subscription; publishing on that ended subscription
fails explicitly. Publishing before subscription or after disposal also fails.

Cancellation passed to publishing reaches Respire. As with other network writes, cancellation
after submission does not prove that Redis did not accept the notification. In-flight
publishes may complete while teardown starts.

The distributed locker is tracked separately in [#1114](https://github.com/thomhurst/Respire/issues/1114).
This package's backplane does not yet implement that contract.
