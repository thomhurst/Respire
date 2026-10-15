---
title: Pub/sub introspection
description: Query active channels, subscriber counts and pattern counts with PUBSUB commands, on one node or every Cluster member.
---

# Pub/sub introspection

Use the Server facet to see which channels are active and how many subscribers they have. These read-only PUBSUB queries do not need `AllowAdmin`.

| Method | Command | Redis |
| --- | --- | --- |
| `PubSubChannelsAsync` | PUBSUB CHANNELS (SHARDCHANNELS with `sharded: true`) | 2.8+ (sharded 7+) |
| `PubSubSubscriberCountsAsync` | PUBSUB NUMSUB (SHARDNUMSUB with `sharded: true`) | 2.8+ (sharded 7+) |
| `PubSubPatternCountAsync` | PUBSUB NUMPAT | 2.8+ |

```csharp
RespireChannel channel = "events";
RespireChannel[] active = await redis.Server.PubSubChannelsAsync(pattern: "event*");
RespireChannelSubscriberCount[] subscribers =
    await redis.Server.PubSubSubscriberCountsAsync([channel]);
long uniquePatterns = await redis.Server.PubSubPatternCountAsync();
RespireChannelSubscriberCount[] shardSubscribers =
    await redis.Server.PubSubSubscriberCountsAsync([channel], sharded: true);
```

- Unsupported versions and denied ACL permissions surface the server error.
- Channel names and patterns keep their exact bytes. Result channels own their storage after the reply and client are disposed.
- Empty names, empty name lists, and an omitted versus empty pattern are distinct.
- `sharded` alone selects the command family; the input channel's kind does not. Returned sharded names have `RespireSubscriptionKind.Sharded`.
- `WithKeyPrefix` and `WithPubSubPrefix` are not applied. Pass `((IRespireNameResolver)view).ResolveChannel(logicalChannel)` for a prefixed literal or sharded count, and `((IRespireNameResolver)view).ResolveChannel(RespireChannel.Pattern(logicalPattern))` for a prefixed CHANNELS pattern.

What the counts mean:

- CHANNELS lists channels with literal subscribers; channels with only pattern subscribers are excluded.
- NUMSUB counts literal subscribers only.
- NUMPAT counts unique patterns, not clients or matching channels.
- Sharded and ordinary counts are separate.
- Results are a snapshot at execution time, not a delivery guarantee.

## Results from every node

Ordinary methods query one node. In Cluster mode they give no global count and do not route sharded queries by channel. Use the `OnAllNodesAsync` methods to query every discovered member, including replicas and primaries without slots:

```csharp
RespireServerResult<RespireChannelSubscriberCount[]>[] nodes =
    await redis.Server.PubSubSubscriberCountsOnAllNodesAsync(["events"]);
foreach (var node in nodes)
{
    if (!node.IsSuccess)
    {
        Console.WriteLine($"{node.Endpoint}: {node.Error!.Message}");
        continue;
    }
    foreach (var item in node.Value)
        Console.WriteLine($"{node.Endpoint}: {item.Channel} has {item.Subscribers} subscribers");
}
```

Results:

- Each result has its `Endpoint` and either `Value` or the original `Error`.
- `Value` on failure throws `InvalidOperationException` with the error as inner exception. `TryGetValue(out var value)` returns false and the default value instead.
- Counts are not summed and names are not deduplicated across nodes. Standalone returns one result.

Discovery:

- Each call runs CLUSTER NODES (needs that ACL permission) on a reachable connection.
- The reporting node keeps its known address. Other members use their advertised hostname if present, else their advertised IP; the seed hostname is never substituted.
- TLS validates against that host unless `TlsOptions.TargetHost` overrides it. Advertised addresses must be reachable and match your certificates.
- Unfinished handshakes are excluded. Missing addresses, malformed or conflicting topology, and discovery failures throw before any node is queried, so you never get a partial topology presented as complete.
- Membership can change after the snapshot; there is no atomic Cluster-wide view.

Execution:

- One temporary connection per endpoint, at most eight active per call. Avoid overlapping polls if you need to limit total connections.
- Connections close on every outcome and are not added to the routing pool. Disposing the client aborts in-progress calls.
- TCP/TLS setup uses `ConnectTimeout`; handshakes and commands use `CommandTimeout` and the response watchdog. If response deadlines are disabled, pass a cancellation deadline to bound a stalled node.
- Authentication and TLS settings apply. A failed node returns an error for its endpoint; other nodes can still succeed.
- Cancellation before or during discovery throws. After discovery, it is recorded per node, and completed results stay available.
- No command is replayed or redirected to another endpoint.

Custom `IRespireServerCommands` implementations, decorators, and mocks must implement or forward all six methods (three single-node methods and their `OnAllNodesAsync` counterparts). These queries are not available in batches or transactions.

Redis contracts: [CHANNELS](https://redis.io/docs/latest/commands/pubsub-channels/),
[NUMSUB](https://redis.io/docs/latest/commands/pubsub-numsub/),
[NUMPAT](https://redis.io/docs/latest/commands/pubsub-numpat/),
[SHARDCHANNELS](https://redis.io/docs/latest/commands/pubsub-shardchannels/), and
[SHARDNUMSUB](https://redis.io/docs/latest/commands/pubsub-shardnumsub/).
