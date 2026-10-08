---
title: SignalR scale-out
description: Share a Respire client across SignalR servers, with Microsoft Redis backplane interoperability.
---

# SignalR scale-out

`Respire.SignalR` implements `HubLifetimeManager<THub>` using Respire's pub/sub connections.
It routes broadcasts, connection and user messages, groups, exclusions, remote group
acknowledgements, and client results across application servers.

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

Map hubs with ASP.NET Core's usual `MapHub<THub>` API. Register the shared concrete
`RespireClient` before resolving a hub lifetime manager. `Respire.DependencyInjection`
registers that client lazily; subscriptions initialize on the first connection or
backplane send. If connection or subscription initialization fails, that operation
fails and a later operation can retry initialization.

The DI container owns a client created by `services.AddRespire`. An externally supplied
client remains owned by its caller. Each hub manager disposes and joins its subscriptions
without disposing the shared client. Dispose application hosts before externally owned
clients. The backplane uses dedicated subscription sockets within that client; it does
not create another command client.

## Migrate from Microsoft's Redis backplane

Regular pub/sub is the default. Its channel names and MessagePack backplane envelopes
interoperate with `Microsoft.AspNetCore.SignalR.StackExchangeRedis`. JSON and MessagePack
hub protocols retain their normal SignalR serialization. MessagePack hub clients require
the usual ASP.NET Core `AddMessagePackProtocol` registration on every participating server.

Keep the hub's fully qualified type name, supported hub protocols, Redis deployment,
and physical channel prefix identical during a rolling migration. For example, Microsoft's
`Configuration.ChannelPrefix = RedisChannel.Literal("myapp:")` matches the Respire
`ChannelPrefix = "myapp:"` above when the client's `PubSubPrefix` is empty. Prefixes are
literal; the adapter does not add a separator. If the client has a `PubSubPrefix`, its
bytes precede the adapter prefix and must be included in Microsoft's physical prefix.
Key prefixes and Redis database numbers do not isolate pub/sub channels.

The integration suite exercises mixed providers in both directions against Microsoft's
.NET 8 and .NET 10 packages, including remote client results. Compatibility with a future
Microsoft backplane envelope change requires verification before upgrading a mixed deployment.

Follow Microsoft's [Redis backplane deployment guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane?view=aspnetcore-10.0),
including its Redis placement recommendations. Configure session affinity where required
by the chosen transports, as described in [SignalR scale-out guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale?view=aspnetcore-10.0).

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

Sharded pub/sub uses Redis 7 or later `SSUBSCRIBE` and `SPUBLISH`, with channels routed
to their slot owners. Every participating application server must enable this mode.
Microsoft's regular Redis backplane and regular Respire subscriptions cannot participate
in that sharded deployment. Ordinary Cluster pub/sub remains available for migration.

## Delivery gaps and pending operations

Redis pub/sub has no replay. Messages published during disconnection or dropped from
a full local subscription buffer are lost. Respire reconnects and resubscribes through
its configured recovery policy; it does not recreate lost SignalR messages or persist them.
Terminal subscription recovery failure is logged as a backplane subscription ending.

Each channel buffers at most `SubscriptionBufferSize` messages, default 1024. Overflow
drops the oldest buffered message. The adapter consumes ordered delivery-gap markers and
logs warnings containing the channel, reason (`Reconnect`, `BufferOverflow`, or both),
and the known local drop count. Reconnect loss counts are unknown. Applications needing
durable delivery should store state separately and let clients reload it after reconnect.

The `Respire.SignalR` meter exposes these counters:

| Instrument | Meaning |
| --- | --- |
| `respire.signalr.delivery.gaps` | Observed gap markers |
| `respire.signalr.messages.dropped` | Known local buffer discards |

Both use `signalr.hub` and `reason` tags. Channel, group, user, and connection identifiers
are absent from metric tags. Register this meter with an OpenTelemetry `AddMeter` call.

Remote group changes await acknowledgement for `GroupAckTimeout` and honor caller
cancellation. A timeout or gap means the operation's outcome can be unknown. Client-result
invocations should always receive a caller cancellation token with a deadline: a remote
server can disappear or a completion can be lost. [Ordinary Cluster `PUBLISH`](https://redis.io/docs/latest/commands/publish/) reports only
node-local receiver counts, so zero receivers cannot establish that a remote client is
missing. Standalone and sharded sends can detect a missing subscriber immediately.
Manager disposal settles pending local waits and stops subscription consumers.

## License and implementation reference

The channel conventions and lifecycle helpers are adapted from the MIT-licensed
[ASP.NET Core Redis backplane at v8.0.31](https://github.com/dotnet/aspnetcore/tree/v8.0.31/src/SignalR/server/StackExchangeRedis).
The package includes Microsoft's copyright and license notice in
`licenses/LICENSE-Microsoft.txt`. Functional controls cover routing, membership, results,
mixed providers, Cluster, cancellation, disposal, reconnects, and buffer overflow.
