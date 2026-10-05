# Subkey notifications as a cache invalidation protocol

Decision: **no-go for replacing `CLIENT TRACKING` with subkey notifications** in Respire's
coherent response cache. This completes the investigation in [#896](https://github.com/thomhurst/Respire/issues/896).
The decision covers Redis 8.8 hash-field notifications and the proposed JSON-path cache.
It does not rule out an explicitly best-effort application cache with a different contract.

Reviewed on 2026-10-05 against Redis 8.8 source, Redis documentation, and Respire commit
`842f3b2b8f08dc33292cb478514c721d8da98385`. An isolated Redis 8.8.3 container supplied the
runtime observations below. No production cache prototype was added: the issue makes that
conditional on establishing equivalent guarantees, and that condition is not met.

## Required contract

Field-level storage already exists: `ReuseHashFields` lets `HGET` and `HMGET` share cached
fields. Every field still depends on the whole physical hash key. A write or tracking
invalidation evicts all of that hash's fields. The proposed change would preserve unaffected
fields after another field changes, rather than merely change the storage layout.

The replacement must preserve stale-fill rejection, negative-result invalidation, local
mutation fences, and continuity handling for disconnects, redirects, and topology retirement.
It must also cover whole-key replacement, deletion, expiration, eviction, and database flushes.
These requirements come from the existing [cache implementation](../src/Respire/RespireClientSideCache.cs),
[partial hash reads](../src/Respire/RespireClient.HashCache.cs), and documented
[consistency boundary](../website/docs/fundamentals/client-side-caching.md#consistency-boundary).
Neither the existing cache nor the proposal can detect an invalidation across an undetected
network partition; the comparison is not a claim of linearizable local reads.

## Findings

### Event coverage is insufficient

Redis documents subkey notifications as Pub/Sub messages with field names, separate from
key-level notifications. Its supported-emitter list currently covers hashes, not JSON paths.
Subkey publication requires a nonempty subkey list; item channels also omit keys containing
a newline. See the [subkey reference](https://redis.io/docs/latest/develop/pubsub/subkeyspace-notifications/)
and [Redis 8.8 notification source](https://github.com/redis/redis/blob/8.8.0/src/notify.c).

In the runtime probe, `HSET` emitted a field notification, but `DEL` emitted only a key-level
event. Both root and path `JSON.SET` emitted `json.set` key-level events without subkey events,
even with the `A` event-class group enabled. A subkey-only cache would miss these mutations.

Adding ordinary key events would require a complete mutation-coverage audit, including
expiration, eviction, rename, replacement, restore, flush, scripts, and modules. It would also
require deployment-wide notification configuration. A successful subscription does not prove
that the server has enabled the necessary events; disabling them does not disconnect subscribers.
This is an additional dependency absent from Respire's tracking handshake.

### Reply ordering does not supply a cache-fill fence

Respire's notification subscriptions use separate Pub/Sub connections and bounded application
buffers. A notification stream's order does not establish an order with completion of a read
on another connection. Consider this legal schedule:

1. `HGET hash field` reads `before`; its reply is held before insertion into the local cache.
2. Another client writes `after` with `HSET`.
3. The subscriber consumes the field event and removes any existing entry.
4. The held `before` reply is inserted. There need not be another event to evict it.

The probe below constructs this schedule explicitly. It is a counterexample to a naive
event-driven cache, not proof that every possible Pub/Sub cache is impossible. Generation
checks spanning the read and insertion could reject this fill, but have to account for
subscription activation, queued delivery, every relevant node, and continuity loss.

Redis's [tracking reference](https://redis.io/docs/latest/develop/reference/client-side-caching/)
describes read registration, invalidation for key changes/expiry/eviction, and RESP3 pushes on
the data connection. Tracking itself still needs client-side race handling. Respire processes
those pushes through `RespireClientSideCache.HandlePush` and uses its existing insertion and
mutation fences; consuming public notification messages does not automatically participate
in those fences.

### Reconnect and Cluster require continuity handling

Pub/Sub has no replay after a disconnect. Respire reports reconnect and buffer-overflow gaps,
but an application observing a gap after draining older messages must not have continued
serving entries whose validity depended on that lost interval. A coherent implementation would
need to disable hits, discard in-flight fills, and flush the affected cache generation at the
transport's continuity boundary, then wait for subscription acknowledgements before refilling.
See [Redis delivery semantics](https://redis.io/docs/latest/develop/pubsub/#delivery-semantics) and
Respire's [subscription buffer](../src/Respire/PubSub/SubscriptionBuffer.cs).

Cluster fan-out **is already implemented** for Respire notification descriptors: exact keys
follow their owning primary; patterns and event descriptors cover all current primaries.
That solves subscription routing, not cache coherence. Notifications are node-local, there is
no merged total order, and ownership transitions may expose gaps or duplicate events.
Any field cache would have to fence reads against the same topology generation and handle
MOVED/ASK and promotion without trusting a partially subscribed set of nodes. See the
[Cluster notification lifecycle](../website/docs/guides/keyspace-notifications.md#delivery-pressure-and-redis-cluster)
and [implementation](../src/Respire/PubSub/SubscriptionHub.ClusterNotifications.cs).

## Runtime evidence and reproduction

The probe used a disposable `redis:8.8-alpine` container reporting `redis_version:8.8.3`, image
ID `sha256:0b2b77d3ea5078274795e3177cdbdada8b96316684a38911d528534ed679b5ec`.
The container was stopped afterward. No shared Redis configuration was changed.

Use separate writer, subscriber, and tracking connections. Await `PSUBSCRIBE` acknowledgement
before mutations. After each acknowledged writer command, send `PING barrier` on the subscribed
connection and collect messages through its `pong` reply. This bounds the observation by protocol
order rather than an arbitrary sleep. For the JSON/whole-key coverage checks, use
`CONFIG SET notify-keyspace-events KSA` and `PSUBSCRIBE __*` so ordinary key events provide a
positive control for notification configuration.

| Probe | Observed result |
| --- | --- |
| `HSET control field value` | Key event `hset` and subkey payload `hset\|5:field` |
| `DEL control` | Key event `del`; no subkey event |
| `JSON.SET document $ '{"field":1}'`, then `JSON.SET document $.field 2` | Each produced key event `json.set`; neither produced a subkey event |
| Enable RESP3 tracking, read a hash field, then delete the hash from the writer | Tracking push `['invalidate', ['hash']]`; no subkey event |
| Hold an `HGET` reply, write a new value, consume the event, then inspect the held reply | Held reply remained `before`; a fresh read returned `after` |
| Disconnect subscriber, mutate, reconnect and acknowledge subscription, then `PING barrier` | No replay of the mutation |
| Set notification flags to the empty string after subscription, then `HSET` | No event and no subscriber disconnect |

The probes used Python's standard-library sockets and assertions, with five-second socket
timeouts. The delayed-fill probe deliberately held an already received reply; it demonstrates
the ordering problem without relying on a timing race. Cluster behavior was assessed from
source and existing documentation, not a new multi-node runtime experiment. Expiration,
eviction, and every module mutation were not exhaustively tested; the observed missing `DEL`
and JSON-path events already reject the proposed equivalence.

## Consequences and reconsideration

Keep whole-key tracking as the correctness boundary. Keep notifications as application events;
do not ignore whole-key tracking invalidations merely because a field event was also received.
The two protocols supply no shared mutation identifier that would prove those messages cover
the same mutation. Retaining whole-key invalidation is safe but does not deliver the requested
field-level retention benefit.

Reconsider a coherent field cache when the server offers a documented subkey tracking protocol
with complete mutation coverage, read-registration/invalidation ordering, whole-key fallbacks,
and defined reconnect and Cluster behavior. Require adversarial tests for stale fills, missed
events, subscription activation, topology changes, expiry, replacement, and JSON-path overlap
before exposing it. A best-effort TTL cache could be a separate proposal, but would not satisfy
the existing response-cache contract or turn this decision into a go.
