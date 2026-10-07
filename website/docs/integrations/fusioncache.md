---
title: FusionCache
description: Share a Respire client between FusionCache's distributed cache, backplane, and distributed locker.
---

# FusionCache

`Respire.FusionCache` provides an `IFusionCacheBackplane` and `IFusionCacheDistributedLocker`
for FusionCache 2.9.0 or later compatible releases. `Respire.Caching` supplies its
`IDistributedCache` L2. All three adapters can share the same caller-owned `IRespireClient`.

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
    .WithRespireBackplane()
    .WithRespireDistributedLocker();

await using var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<IFusionCache>();
await cache.GetOrSetAsync("product:42", _ => Task.FromResult(42));
```

The service provider disposes the cache, its backplane, and its locker before the caller
disposes `client`. None of the adapters disposes that externally supplied client. The backplane uses
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

A terminal subscription end is logged, rather than reported as a reconnect notification.
It does not clear the instance's subscription state automatically. To reuse the backplane,
await `UnsubscribeAsync` (or call `Unsubscribe`), then call `SubscribeAsync` (or `Subscribe`)
again once the shared client can connect. If the caller has disposed that client or its
pub/sub reconnect policy has been exhausted, create a replacement client and a new
backplane instance instead. Exhaustion permanently rejects new subscriptions on that
client, even if Redis becomes available again.

Cancellation passed to publishing reaches Respire. As with other network writes, cancellation
after submission does not prove that Redis did not accept the notification. In-flight
publishes may complete while teardown starts.

## Distributed locking

`RespireFusionCacheDistributedLocker` implements the sync and async acquisition/release
contract verified against [FusionCache 2.9.0](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/src/ZiggyCreatures.FusionCache/Locking/Distributed/IFusionCacheDistributedLocker.cs).
It uses `Respire.Coordination` fenced leases on the supplied client. Contended acquisitions
poll with a bounded delay; both RESP2 and RESP3 work without client-side tracking. Zero
timeout makes one immediate attempt, a positive timeout bounds the wait including Redis
attempts, and `Timeout.InfiniteTimeSpan` waits until acquisition, cancellation, or disposal.
Contention/timeout returns `null`; caller cancellation throws `OperationCanceledException`.
Network failures retain Respire's normal error behavior. Cleanup can outlast the acquisition
budget because an acquired lease must stop renewal and release safely.
Synchronous acquisition and release block the calling thread until their asynchronous
operations, including cleanup, finish.

The default lease is 30 seconds and is renewed halfway through each duration. Set
`RespireFusionCacheDistributedLockerOptions.LeaseDuration` between one second and five minutes;
`PollInterval` defaults to 50 milliseconds and accepts one millisecond to one second. These
options apply to the constructor, `WithRespireDistributedLocker`, and
`AddFusionCacheRespireDistributedLocker`. The wait timeout does not set the server lease duration.
Managed renewal fences uncertain commands with `CLIENT ID` and `CLIENT KILL`; Redis ACLs
must permit them as well as the underlying script, counter, and owner-checked lock commands.

Lease identity combines the cache name and FusionCache's lock name, which already includes
its cache key prefix and lock suffix. Cache instance IDs and operation IDs are diagnostics,
so different nodes contend for the same lease. Length-framed UTF-16 code units are hashed
with SHA-256, producing `respire:fusioncache:lock:{HASH}:lease` and
`respire:fusioncache:lock:{HASH}:counter` before the client's key prefix. Both keys share a
Cluster slot. Use the same cache name, lock-name settings, client prefix, and database on
communicating nodes. Different cache names isolate leases; L2 data and backplane traffic
still need their own prefixes as described above.

Each acquisition increments a persistent counter. Release and renewal compare the generated
owner token, so a stale handle cannot delete or extend a replacement lease. Never delete,
expire, evict, or reset the counter: configure retention and memory capacity accordingly.
Counters grow with the number of distinct lock identities. Redis asynchronous failover,
restoration, or history loss can roll back counters, and a lost acquisition reply can consume
a token and leave a lease until its bounded expiry. This is not a consensus-backed service.

Release, caller cancellation, renewal loss, and locker disposal stop renewal and join cleanup.
Cleanup uses its own token even when the factory's token is already cancelled; a transport
failure leaves server expiry as the fallback. Concurrent teardown callers join the same work.
Explicit release and teardown propagate server or protocol rejection to FusionCache, including
when `ReThrowDistributedLockerExceptions` is enabled. Automatic cancellation cleanup observes
the same failure in the background and logs it when a logger is configured.
Locker disposal also joins acquisitions still returning or releasing a lease. Await disposal
before closing the shared client.
The provider owns the locker created by `WithRespireDistributedLocker`, including renewal
handles left active by an interrupted operation. `AddFusionCacheRespireDistributedLocker`
registers transient provider-owned lockers for `WithRegisteredDistributedLocker` discovery.
Neither registration creates or disposes a command client. For direct construction, retain
and dispose your `RespireFusionCacheDistributedLocker` after using `SetupDistributedLocker`:
FusionCache's own disposal only detaches its locker.

This reduces cross-node cache stampedes while ownership remains valid. FusionCache's
object-based locker interface does **not** pass the fencing token to a factory, atomically
enforce that token on arbitrary cache or database writes, or cancel a factory after ownership
loss. A slow factory or background factory completion can continue after expiry, cancellation,
or a connection failure and overlap a new owner. The diagnostic `RespireFusionCacheLock`
handle exposes its token and ownership cancellation token, but FusionCache cannot enforce
them automatically. There is no exactly-once execution guarantee. For writes that require
fencing, use `Respire.Coordination` directly and make the protected resource atomically reject
stale fencing tokens. Configure FusionCache's fail-safe, lock timeout, exception, and background
completion policies for your application's tolerance for duplicate factory execution.
