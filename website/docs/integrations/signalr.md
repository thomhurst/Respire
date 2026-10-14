---
title: SignalR scale-out
description: Share a Respire client across SignalR servers, with Microsoft Redis backplane interoperability.
---

# SignalR scale-out

`Respire.SignalR` is a SignalR backplane (`HubLifetimeManager<THub>`) on Respire pub/sub. It
routes broadcasts, connection and user messages, groups, and client results across servers, and
interoperates with Microsoft's Redis backplane.

```bash
dotnet add package Respire.SignalR
dotnet add package Respire.DependencyInjection
```

## Register the backplane

```csharp
using Respire.DependencyInjection;
using Respire.SignalR;

builder.Services.AddRespire("redis://localhost");
builder.Services.AddSignalR().AddRespire(options =>
{
    options.ChannelPrefix = "myapp:";
    options.SubscriptionBufferSize = 1024;
    options.GroupAckTimeout = TimeSpan.FromSeconds(30);
});
```

Map hubs with the usual `MapHub<THub>`.

- Register the shared `RespireClient` before a hub lifetime manager is resolved.
- Subscriptions start on the first connection or send. If that fails, the operation fails and a
  later one retries.
- The container owns a client created by `services.AddRespire`; you own a client you supply. The
  backplane never disposes the client, so dispose the host before a client you own.

| Option | Default | Meaning |
| --- | --- | --- |
| `ChannelPrefix` | `""` | Literal channel prefix; no separator is added |
| `SubscriptionBufferSize` | 1024 | Messages buffered per channel; overflow drops the oldest |
| `GroupAckTimeout` | 30 seconds | How long a remote group change waits for acknowledgement |
| `RemoteClientResultTimeout` | 30 seconds | How long a receiving server keeps forwarded client-result state (up to 4,294,967,294 ms) |
| `UseShardedPubSub` | `false` | Use sharded pub/sub (see [Cluster](#cluster-and-sharded-pubsub)) |

## Migrate from Microsoft's Redis backplane

Regular pub/sub is the default and interoperates with
`Microsoft.AspNetCore.SignalR.StackExchangeRedis` (.NET 8 and .NET 10, in both directions,
including client results). JSON and MessagePack hub protocols work as usual; MessagePack needs
`AddMessagePackProtocol` on every server.

During a rolling migration, keep these identical on every server:

- The hub's fully qualified type name
- The supported hub protocols
- The Redis deployment
- The physical channel prefix

Microsoft's `Configuration.ChannelPrefix = RedisChannel.Literal("myapp:")` matches
`ChannelPrefix = "myapp:"` when the client's `PubSubPrefix` is empty. A `PubSubPrefix` comes
before the adapter prefix, so include it in Microsoft's prefix. Key prefixes and database numbers
do not isolate channels.

If a future Microsoft release changes its message format, verify compatibility before upgrading a
mixed deployment. Follow Microsoft's
[Redis backplane guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane?view=aspnetcore-10.0)
and configure session affinity where your transports need it
([scale-out guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale?view=aspnetcore-10.0)).

## Cluster and sharded pub/sub

Configure the shared client for Cluster, then opt into sharded pub/sub:

```csharp
using Respire.DependencyInjection;
using Respire.SignalR;

builder.Services.AddRespire(_ => RespireOptions.Parse("redis://localhost:7000")
    with { UseCluster = true });
builder.Services.AddSignalR().AddRespire(options =>
{
    options.ChannelPrefix = "myapp:";
    options.UseShardedPubSub = true;
});
```

Sharded pub/sub uses `SSUBSCRIBE`/`SPUBLISH` (Redis 7+). Every server must enable it; Microsoft's
backplane and regular Respire subscriptions cannot join a sharded deployment. Regular Cluster
pub/sub stays available for migration.

## Delivery gaps and pending operations

Redis pub/sub has no replay. Messages published while disconnected, or dropped from a full
buffer, are lost; Respire resubscribes but does not recreate them. For durable delivery, store
state separately and let clients reload it after reconnecting.

Each gap is logged as a warning with the channel, reason (`Reconnect`, `BufferOverflow`, or both),
and the local drop count (unknown for reconnects). A subscription that cannot recover is logged as
ended. The `Respire.SignalR` meter (register with `AddMeter`) exposes:

| Instrument | Meaning |
| --- | --- |
| `respire.signalr.delivery.gaps` | Observed gaps |
| `respire.signalr.messages.dropped` | Known local buffer discards |

Both have `signalr.hub` and `reason` tags, and no channel, group, user, or connection IDs.

Pending operations:

- Remote group changes wait up to `GroupAckTimeout` and honor cancellation. After a timeout or
  gap, the outcome can be unknown.
- Always pass client-result invocations a cancellation token with a deadline. A remote server can
  disappear, a completion can be lost, and Microsoft's provider sends no cancellation.
- The receiving server drops forwarded client-result state after `RemoteClientResultTimeout` and
  sends an error completion if it can. This does not stop the client handler. Debug event
  `RemoteResultExpired` (18) logs the invocation ID and `timeout` or `disconnect`.
- Cancelling on a Respire origin cancels the invocation task, even after publishing. Late
  completions are ignored.
- On Cluster, [regular `PUBLISH`](https://redis.io/docs/latest/commands/publish/) counts only
  node-local receivers, so zero receivers does not prove a client is missing. Standalone and
  sharded sends detect a missing subscriber immediately.
- Disposing the manager settles pending local waits.

## License and implementation reference

The channel conventions and lifecycle helpers are adapted from the MIT-licensed
[ASP.NET Core Redis backplane at v8.0.31](https://github.com/dotnet/aspnetcore/tree/v8.0.31/src/SignalR/server/StackExchangeRedis).
This link identifies the original source version. The interoperability tests run
against `Microsoft.AspNetCore.SignalR.StackExchangeRedis` 8.0.32 on .NET 8 and
10.0.13 on .NET 10.
The package includes Microsoft's copyright and license notice in
`licenses/LICENSE-Microsoft.txt`.
