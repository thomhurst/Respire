---
title: Status and roadmap
description: What Respire supports today and what remains before a stable release.
---

# Status and roadmap

Respire is pre-release. Everything below is implemented, but public APIs may change.
Each linked guide lists its server and protocol requirements.

Six settling surfaces now require an explicit [experimental API opt-in](experimental-apis.md):
VectorData, semantic chat caching, hedged reads, hash import sessions, deferred raw commands,
and StackExchange.Redis compatibility. These remain outside the 1.0 compatibility promise.

Public interface compatibility and the post-1.0 extension policy are described in
[Interface compatibility](intro.md#interface-compatibility).

## Available now

- [Polly v8 and Microsoft.Extensions.Resilience](guides/external-resilience.md) integration
  through the optional `Respire.Resilience` package, with a sample proving one retry layer.

- [Tested Redis recipes](guides/recipes.mdx) for leaderboards, expiring sessions,
  a transactional stream outbox, leader election, and environment-based connections.

- [Source-generated logging with stable event IDs](integrations/log-events.md) across shipping
  packages, with duplicate-ID and logging-contract regression checks.

- [Span formatting and parsing](fundamentals/values-and-serialization.md#span-formatting-and-parsing)
  for keys and values, including allocation-free UTF-8 interpolation.
- [Public API baselines and deprecation policy](packages.md#public-api-review-and-deprecation)
  cover all shipping packages, with build failures for undeclared API changes.

### Connections, routing, and recovery

- [Connections](fundamentals/connections.md) from Redis URIs, StackExchange.Redis connection
  strings, or `RespireOptions`, with lazy or eager connect, TLS, and automatic pipelining.
- [Independent liveness bounds](fundamentals/connections.md#independent-liveness-bounds) for
  receive progress and large borrowed `SET` writes.
- [RESP3 preference](fundamentals/connections.md#protocol-negotiation) with RESP2 fallback and
  explicit protocol overrides.
- [Redis Cluster](fundamentals/connections.md#redis-cluster-endpoint-identity) routing with
  topology refresh, redirect recovery, and same-slot transactions.
- [Automatic Sentinel failover](fundamentals/connections.md#redis-sentinel), validated with `ROLE`,
  plus recovery after disconnects or `READONLY` replies.
- [Replica selection](fundamentals/connections.md#read-from-replicas) with `ReadFrom` and
  `WithReadFrom`, including nearest reads, [hedged reads](fundamentals/connections.md#hedged-reads),
  and [availability-zone affinity](fundamentals/connections.md#availability-zone-affinity).
- [Reconnect policies](guides/reconnect-policy.md), automatic pub/sub resubscription,
  [maintenance notifications and handoffs](fundamentals/connections.md#maintenance-notifications),
  and health-checked [failover groups](guides/failover-groups.md) across deployments.
- Opt-in [endpoint circuit breakers](guides/circuit-breakers.md) reject commands to a failing
  endpoint on standalone, Sentinel, and Cluster clients. They [compose with command retries](guides/circuit-breakers.md#retries): an open
  endpoint is never retried.
- [Renewable credentials](guides/renewable-credentials.md), separate Sentinel authentication,
  and [Azure Managed Redis](guides/azure-managed-redis.md) / [AWS IAM](guides/aws-iam-credentials.md) adapters.

### Commands and coordination

- GC-owned raw, script, and function results by default, with explicit
  [pooled result leases](guides/raw-commands.md#result-lifetime) and a shared
  `await using` pattern for batches and transactions.

- Typed facets for strings, keys, collections, streams, bitmaps, HyperLogLog, geo, scripts, and
  server commands; a [command catalog with raw and interpolated execution](guides/raw-commands.md);
  and [source-generated custom commands](guides/generated-commands.md).
- [Typed Redis 8.10 / Valkey 9.1 commands](guides/raw-commands.md#finding-typed-apis), including
  [sparse arrays](commands/arrays.md) and [hash import sessions](guides/hash-import.md).
  Availability depends on the server version.
- [Explicit-node lifecycle and Cluster administration](guides/server-node-lifecycle.md),
  [Sentinel administration](guides/sentinel-administration.md), and
  [client filters](guides/client-administration.md#filtering-and-closing-clients).
- [Typed LCS matches](commands/strings-and-keys.md#longest-common-subsequence).
- [Blocking queues](guides/blocking-queues.md) and [streamed string transfers](commands/strings-and-keys.md)
  on dedicated connections.
- [Typed immediate job queues](guides/job-queues.md) with bounded enqueue deduplication, scoped
  handlers, visibility recovery and dead-letter completion.
- [Hosted stream consumers](guides/stream-workers.md) with bounded concurrency, scoped typed
  handlers, idle recovery, delivery limits, dead-lettering, graceful draining, metrics, and
  W3C trace propagation.
- [Idempotent stream appends](guides/command-retries.md#idempotent-stream-appends): on Redis 8.6+,
  a retried `XADD` cannot duplicate the entry.
- [Batches, transactions, and `WATCH`](guides/batches-and-transactions.md), plus
  [durability acknowledgements](guides/durability-acknowledgements.md).
- [Pub/sub](guides/pub-sub.md) with pattern and sharded subscriptions and delivery-gap reporting.
  [Keyspace, keyevent, and Redis 8.8 subkey notifications](guides/keyspace-notifications.md) work
  on Cluster. Reconnects can lose notifications; topology changes can lose or duplicate them.
- [Distributed locks](guides/distributed-locks.md) and [coordination](guides/coordination.md):
  fencing tokens, leases, semaphores, and fixed-window, sliding-window, token-bucket and
  [GCRA rate limiting](guides/coordination.md#gcra).
- Typed [JSON](guides/json.md), [Search](guides/search.md), [TimeSeries](guides/timeseries.md),
  [probabilistic](guides/probabilistic.md), and [vector-set](guides/vector-sets.md) APIs.
- Source-generated [JSON mappers](guides/generated-json-mappers.md),
  [hash codecs](guides/generated-hash-codecs.md), and
  [Search schemas](guides/generated-search-schemas.md), with a
  [Native AOT sample](guides/generated-hash-codecs.md#native-aot-conformance-sample) for .NET 8 and .NET 10.
- [Generated typed Search queries](guides/generated-search-schemas.md#typed-queries) for hash and JSON
  models: filters, sorting, paging, field selection, aggregation, and filtered KNN without reflection.

### Caching, integration, and testing

- [Typed near-cache maps](guides/near-cache-map.md) with read-through loaders, write-through
  or batched write-behind persistence, preload, shared invalidation, and hash field expiry.
- [Semantic chat response caching](guides/semantic-cache.md) for `Microsoft.Extensions.AI`.
- [VectorData hash and JSON collections](guides/vector-data.md) with AOT-friendly mapping, KNN,
  filters, and hybrid search on Redis 8.4+. The guide lists unsupported features.
- RESP3 [server-assisted client-side caching](fundamentals/client-side-caching.md) with OPTIN or
  BCAST/prefix tracking.
- [Cache-aside factories](fundamentals/client-side-caching.md#populate-missing-values-with-a-factory)
  with relative or absolute server expiry and optional concurrent-miss coalescing.
- Typed serialization, custom serializers, and [value compression codecs](guides/value-codecs.md).
- [Dependency injection](integrations/dependency-injection.md),
  [distributed caching and `HybridCache`](integrations/caching.md) with cross-instance tag
  invalidation, and [OpenTelemetry](integrations/observability.md), including a
  [Prometheus and Redis dashboard setup](integrations/observability.md#prometheus-and-the-published-redis-dashboard).
- [Configuration binding and named options](integrations/dependency-injection.md#bind-appsettingsjson-and-validate-at-startup),
  with a generated builder covering every client option and validation at host startup.
- An [in-memory test server](guides/in-memory-testing.md), [Redis/Valkey container fixtures](guides/testing-containers.md),
  and a [test sample](guides/testing-sample.md).
- Samples: [.NET Aspire](integrations/aspire-sample.md),
  [ASP.NET Core](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.AspNetCore),
  [Cluster](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Cluster), and
  [Sentinel](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Sentinel), with a
  [topology smoke workflow](https://github.com/thomhurst/Respire/blob/main/samples/TopologySmoke.md).

### Compatibility adapters

- The [StackExchange.Redis migration adapter](./guides/stackexchange-interop) (`Respire.StackExchangeCompat`)
  covers a documented subset of `IConnectionMultiplexer`, `IDatabase`, `IBatch`, `ITransaction`,
  `IServer`, and `ISubscriber`, plus value conversion and a raw-command bridge. It passes the
  upstream Microsoft distributed cache and DataProtection tests and
  [Hangfire acceptance](./guides/hangfire-acceptance). It is not full StackExchange.Redis parity.
- Microsoft's SignalR Redis backplane does not run on the adapter, and support is not planned. Use
  [`Respire.SignalR`](./integrations/signalr).
- The [Orleans 10.2.2 acceptance suite](./guides/orleans-compatibility) runs the Orleans Redis
  clustering, grain storage, and reminder providers on the adapter. Full silo integration,
  Cluster routing, and failover are not validated.

### Retries and write outcomes

- Opt-in [command retry policies](./guides/command-retries) through `RespireOptions.RetryPolicy`.
  Each command has a retry category that decides whether it may be resent after it might have run.
  Retries cover immediate commands, raw/script/function requests with a declared category,
  failover groups, whole batches and unwatched transactions, and
  [watched transaction helpers](guides/batches-and-transactions.md#automatic-watched-transaction-retries).
  An enabled policy adds latency and allocation to every eligible command.
- The [transport write evidence log](./guides/transport-write-evidence) records how Respire tells
  an unsent command from one that may have reached Redis.

A notification-based field cache is not offered: subkey notifications cannot meet the cache
consistency contract. See the [field-level caching decision](guides/keyspace-notifications.md#field-level-cache-decision).

## Planned work

These epics track the work before 1.0. They are not release commitments.

- [1.0 release gate](https://github.com/thomhurst/Respire/issues/1567)
- [API breaks before 1.0](https://github.com/thomhurst/Respire/issues/1576)
- [Onboarding, docs, and developer experience](https://github.com/thomhurst/Respire/issues/1585)
- [Features from other Redis clients](https://github.com/thomhurst/Respire/issues/1594),
  including a [reliable job queue on `Respire.Streaming`](https://github.com/thomhurst/Respire/issues/1595)

See [Coming from StackExchange.Redis](./stackexchange-redis) for a feature comparison and the
[introduction](./intro.md#design-bets) for the design bets. Report issues on
[GitHub](https://github.com/thomhurst/Respire/issues).
