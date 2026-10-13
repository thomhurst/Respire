---
title: FusionCache
description: Share a Respire client between FusionCache's distributed cache, backplane, and distributed locker.
---

# FusionCache

`Respire.FusionCache` provides a FusionCache 2.9.0+ backplane (`IFusionCacheBackplane`) and
distributed locker (`IFusionCacheDistributedLocker`). `Respire.Caching` supplies the
`IDistributedCache` L2. All three can share one `IRespireClient` that you own.

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

- Dispose the service provider before the client. No adapter disposes a client you supply.
- The serializer comes from FusionCache. Configure it, including any AOT needs, separately from
  Respire's serializer.
- Each cache needs its own backplane instance, even with a shared client.
  `WithRespireBackplane` creates one per cache. For direct construction, pass a
  `RespireFusionCacheBackplane` to `SetupBackplane`. For registered discovery, call
  `AddFusionCacheRespireBackplane` (transient) and FusionCache's `WithRegisteredBackplane`.

## Channels and compatibility

- The backplane uses regular `SUBSCRIBE`/`PUBLISH` (not sharded) on the channel FusionCache
  chooses, over RESP2 or RESP3.
- It interoperates with `ZiggyCreatures.FusionCache.Backplane.StackExchangeRedis` 2.9.0.
- Communicating nodes need the same cache name, channel prefix, L2 key prefix, and serializer.
  Give unrelated applications distinct names and prefixes.

Pub/sub ignores the database number, and `WithKeyPrefix` does not prefix channels. A database
or L2 key prefix alone does not isolate backplane traffic.

## Delivery gaps and lifecycle

Respire reconnects and resubscribes automatically. After a reconnect or buffer overflow, the
backplane logs the gap and calls FusionCache's connection handler with `IsReconnection = true`
(the initial subscription reports `false`). Redis pub/sub cannot replay lost messages, so delivery
is not durable; FusionCache applies its own recovery.

Pass `RespireSubscriptionOptions` to the constructor or registration methods to set buffer size
and overflow policy; unset values inherit the client settings. Callbacks run in order on a
background consumer, so slow callbacks can fill the buffer. Malformed messages and callback
exceptions are logged and do not block later messages.

Lifecycle rules:

- Await unsubscribe before subscribing the same instance again. Subscribing an active or
  still-retiring instance fails.
- Unsubscribe and disposal wait for running callbacks to finish, so callbacks must return.
- Publishing before subscribe, after disposal, or after the subscription ends fails.
- Disposing the client or exhausting its reconnect policy ends the subscription. To reuse the
  backplane after an end, unsubscribe, then subscribe again once the client can connect. If the
  client was disposed or its pub/sub reconnect policy is exhausted, create a new client and a
  new backplane: exhaustion permanently rejects new subscriptions on that client.
- Cancelling a publish after it was sent does not prove Redis rejected the message.

## Distributed locking

`RespireFusionCacheDistributedLocker` implements the sync and async contract of
[FusionCache 2.9.0](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/src/ZiggyCreatures.FusionCache/Locking/Distributed/IFusionCacheDistributedLocker.cs)
with `Respire.Coordination` fenced leases. It works on RESP2 and RESP3 without client-side tracking.

| Acquisition timeout | Behavior |
| --- | --- |
| Zero | One immediate attempt |
| Positive | Waits up to the timeout, including Redis calls |
| `Timeout.InfiniteTimeSpan` | Waits until acquired, cancelled, or the locker is disposed |

Contention or timeout returns `null`. Caller cancellation throws `OperationCanceledException`.
Network failures throw as usual. Cleanup can take longer than the acquisition timeout.

:::warning Synchronous calls block threads
Synchronous acquire, release, and dispose block until the async work and cleanup finish; with an
infinite timeout, the thread stays blocked until the wait ends. Many synchronous contenders can
starve the thread pool. Use async entry points such as `GetOrSetAsync`, and await `DisposeAsync`.
:::

### Options

`RespireFusionCacheDistributedLockerOptions` applies to the constructor,
`WithRespireDistributedLocker`, and `AddFusionCacheRespireDistributedLocker`.

| Option | Default | Range and meaning |
| --- | --- | --- |
| `LeaseDuration` | 30 seconds | 1 second to 5 minutes. Server lease, renewed halfway through. Independent of the wait timeout. |
| `PollInterval` | 50 ms | 1 ms to 1 second. Delay between contended attempts, randomized by ±10% (minimum 1 ms, capped by the remaining wait). The first attempt is immediate. |
| `ReleaseOnCallerCancellation` | `true` | Whether cancelling the acquisition token after acquisition releases the lease. See [lease lifetime](#lease-lifetime). |
| `MaximumIndependentLeaseLifetime` | `null` (no limit) | Total lifetime cap for a lease that outlives caller cancellation. See [lease lifetime](#lease-lifetime). |

Redis ACLs must permit `CLIENT ID` and `CLIENT KILL`, which renewal uses to fence uncertain
commands, plus the script, counter, and lock commands.

### Keys and isolation

The lease identity is the cache name plus FusionCache's lock name (which includes its key prefix
and lock suffix). Instance and operation IDs are not part of it, so all nodes contend for the same
lease. The identity is hashed with SHA-256 into two keys in one Cluster slot, before the client's
key prefix:

- `respire:fusioncache:lock:{HASH}:lease`
- `respire:fusioncache:lock:{HASH}:counter`

Communicating nodes need the same cache name, lock-name settings, client prefix, and database.
Different cache names isolate leases; L2 data and backplane traffic need their own prefixes.

:::warning Never delete the counter
Each acquisition increments a persistent counter, and release and renewal check the owner token,
so a stale handle cannot delete or extend a newer lease. Never delete, expire, evict, or reset the
counter, and size memory and retention for one counter per distinct lock. Asynchronous failover,
restore, or data loss can roll counters back. A lost acquisition reply can leave a lease until it
expires. This is not a consensus-backed lock service.
:::

### Release and disposal

- Release, renewal loss, and locker disposal stop renewal and wait for cleanup.
- Release ignores its cancellation token, so cancellation never replaces the factory's result or
  exception. If cleanup cannot reach Redis, the lease expires on the server.
- Explicit release reports server or protocol rejection to FusionCache. Locker disposal reports
  cleanup failures of handles still held.
- Cleanup after automatic cancellation runs in the background. Configure a logger to see its
  failures; a later explicit release of that handle also reports them.
- Await locker disposal before closing the shared client.
- `WithRespireDistributedLocker` and `AddFusionCacheRespireDistributedLocker` (transient, for
  `WithRegisteredDistributedLocker`) create provider-owned lockers. With `SetupDistributedLocker`,
  dispose the locker yourself: FusionCache only detaches it.

### Lease lifetime

By default the acquisition token also controls the lease: cancelling a request-scoped token after
acquisition releases the lease, even if the factory keeps running.

With `ReleaseOnCallerCancellation = false`, the token only bounds the wait. The lease renews until
explicit release, locker disposal, or ownership loss. This suits FusionCache's
`AllowTimedOutFactoryBackgroundCompletion`: a timed-out factory moves to the background with its
lock handle, and the lease keeps renewing after the request is cancelled. Foreground cancellation
still releases immediately. See FusionCache's
[factory timeout helper](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/src/ZiggyCreatures.FusionCache/Internals/RunUtils.cs)
and [background handoff](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/src/ZiggyCreatures.FusionCache/FusionCache.cs).

If background completion never finishes, an independent lease renews indefinitely. To cap it, set
`MaximumIndependentLeaseLifetime`. With a shared `IRespireClient` registered:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Respire.FusionCache;

var lockerOptions = new RespireFusionCacheDistributedLockerOptions
{
    ReleaseOnCallerCancellation = false,
    LeaseDuration = TimeSpan.FromSeconds(30),
    MaximumIndependentLeaseLifetime = TimeSpan.FromMinutes(5),
};
builder.Services.AddFusionCache().WithRespireDistributedLocker(lockerOptions);
```

- The value must be at least 1 ms (up to `TimeSpan.MaxValue`) and requires
  `ReleaseOnCallerCancellation = false`. Invalid combinations throw at construction or registration.
- The clock starts when acquisition succeeds and covers foreground and background work together.
  It is separate from FusionCache's lock timeout and from `LeaseDuration`.
- On expiry, `RespireFusionCacheLock.LifetimeLimitExpired` becomes `true`, its
  `OwnershipCancellationToken` is cancelled, and the lease is released. `OwnershipLost` and
  `RenewalFailure` are not set. Release is not real-time and never removes a newer owner's lease.
- After expiry another node can acquire the lock while the original factory still runs, and
  FusionCache can still publish that factory's result.

:::danger No exactly-once execution
A slow or background factory can keep running after expiry, cancellation, or a connection failure,
and overlap a new owner. FusionCache does not pass the fencing token to the factory, enforce it on
writes, or cancel the factory when ownership is lost. The `RespireFusionCacheLock` handle exposes
its token and `OwnershipCancellationToken`, but nothing enforces them. For writes that need
fencing, use `Respire.Coordination` directly and make the resource reject stale tokens. Set
FusionCache's fail-safe, lock timeout, exception, and background completion policies for how much
duplicate factory execution you can tolerate.
:::
