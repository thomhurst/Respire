# Pub/sub introspection

The Server facet exposes PUBSUB CHANNELS, NUMSUB, NUMPAT, SHARDCHANNELS, and
SHARDNUMSUB. Ordinary introspection requires Redis 2.8+; sharded introspection
requires Redis 7+. Unsupported versions and denied ACL permissions retain server
errors. These read-only commands do not require `AllowAdmin`.

```csharp
RespireChannel channel = "events";
RespireChannel[] active = await redis.Server.PubSubChannelsAsync(pattern: "event*");
RespireChannelSubscriberCount[] subscribers =
    await redis.Server.PubSubSubscriberCountsAsync([channel]);
long uniquePatterns = await redis.Server.PubSubPatternCountAsync();
RespireChannelSubscriberCount[] shardSubscribers =
    await redis.Server.PubSubSubscriberCountsAsync([channel], sharded: true);
```

Channel names and patterns preserve their exact bytes. `RespireChannel` owns its
input storage; result channels also own their storage after the reply and client
are disposed. The span of names is copied before asynchronous work. Empty names,
empty name lists, and an omitted versus empty pattern remain distinct. The
`sharded` argument selects the command family; the input channel's subscription
kind does not change the query. Returned sharded names have `SubscriptionKind.Sharded`.

Server introspection does not apply `WithKeyPrefix` to channel names or patterns.
It inspects the server's pub/sub namespace. CHANNELS lists channels with literal
subscribers, excluding channels with only pattern subscribers. NUMSUB counts
literal subscribers, excluding pattern subscribers. NUMPAT counts unique patterns,
not the number of clients or matching channels. Sharded and ordinary counts are
separate. These are observations at execution time, not a durable delivery guarantee.

## Results from every node

Ordinary methods query one execution node. In Cluster mode they do not imply a
global count or route sharded introspection by the supplied channel. Use the explicit
`OnAllNodesAsync` methods to inspect every discovered member, including replicas
and primaries without slots:

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

Each result retains its endpoint and either an owned `Value` or the original
`Error`. Accessing `Value` on failure throws `InvalidOperationException` with that
error as its inner exception. Counts are not summed and channel names are not
deduplicated across nodes. Standalone execution returns one result.
`TryGetValue(out var value)` provides non-throwing access; on failure it returns
false and assigns the default value, while `Error` retains the original exception.

Cluster discovery uses CLUSTER NODES on a reachable connection, requiring that ACL
permission. It keeps the reachable address of the reporting node and uses advertised
client endpoints for other members; deployment networking must make those addresses
reachable. Unfinished handshake entries are excluded. Missing addresses, malformed
or conflicting topology, and discovery failures throw before fan-out starts, so a
partial topology is never silently presented as complete. Membership can change
after the snapshot; no atomic Cluster-wide observation is promised.

Each call re-runs CLUSTER NODES and opens one temporary dedicated connection per
discovered endpoint, with at most eight nodes active at once. This limit applies
per call; avoid overlapping polls when controlling total connection demand. These pools
are scoped to the call and closed on every outcome, including success. They do not
retain historical replicas or add transports to the routing pool cache. Client
disposal tracks and aborts active fan-out pools, including pending handshakes.
Connection setup uses `ConnectTimeout`; commands use `CommandTimeout` and the
configured response watchdog. Caller cancellation also covers nodes awaiting capacity.

Execution preserves authentication/TLS. A failed node produces an endpoint-associated
error while other nodes can succeed. Cancellation before or during discovery throws. After discovery,
cancellation is recorded in the affected node results; already completed successes
remain available. No command is replayed or redirected
away from its target endpoint.

Custom `IServerCommands` implementations and decorators must implement or forward
all six new methods. No deferred command facet is added.

Redis contracts: [CHANNELS](https://redis.io/docs/latest/commands/pubsub-channels/),
[NUMSUB](https://redis.io/docs/latest/commands/pubsub-numsub/),
[NUMPAT](https://redis.io/docs/latest/commands/pubsub-numpat/),
[SHARDCHANNELS](https://redis.io/docs/latest/commands/pubsub-shardchannels/), and
[SHARDNUMSUB](https://redis.io/docs/latest/commands/pubsub-shardnumsub/).
