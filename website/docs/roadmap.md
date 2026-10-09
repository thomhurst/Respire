---
title: Status and roadmap
description: What Respire supports today and what remains before a stable release.
---

# Status and roadmap

Respire is pre-release. The features below are implemented, but public APIs may still change.
Server and protocol requirements vary by feature; each linked guide describes its limits.

## Available now

### Connections, routing, and recovery

- [Connections](fundamentals/connections.md) through Redis-style URIs, StackExchange.Redis connection
  strings, or `RespireOptions`, with lazy/eager connection, TLS, and automatic pipelining.
- [RESP3 preference](fundamentals/connections.md#protocol-negotiation) with bounded RESP2 fallback,
  explicit protocol overrides, and typed reply normalization.
- [Redis Cluster](fundamentals/connections.md#redis-cluster-endpoint-identity) routing with periodic
  topology refresh, redirect recovery, and same-slot transactions.
- [Automatic Sentinel failover](fundamentals/connections.md#redis-sentinel): monitors subscribe to
  primary-switch and down events, rediscover the service, and validate the replacement with `ROLE`.
  Reactive recovery remains available after disconnects or `READONLY` replies.
- [Replica selection](fundamentals/connections.md#read-from-replicas) through `ReadFrom` and
  `WithReadFrom`, including nearest reads, [hedged reads](fundamentals/connections.md#hedged-reads),
  and [availability-zone affinity](fundamentals/connections.md#availability-zone-affinity).
- [Reconnect policies](guides/reconnect-policy.md), automatic pub/sub resubscription,
  [maintenance notifications and handoffs](fundamentals/connections.md#maintenance-notifications),
  and health-checked [failover groups](guides/failover-groups.md) across deployments.
- [Opt-in circuit breakers](guides/circuit-breakers.md) reject standalone and Cluster immediate,
  batch, and transaction commands during endpoint failures, with independent Cluster data-node
  histories and bounded half-open probes. Sentinel and remaining resilience integration stay
  tracked by [the circuit epic](https://github.com/thomhurst/Respire/issues/1255).
- [Renewable credentials](guides/renewable-credentials.md), independent Sentinel authentication,
  and [Azure Managed Redis](guides/azure-managed-redis.md) / [AWS IAM](guides/aws-iam-credentials.md) adapters.

### Commands and coordination

- Typed string, key, collection, stream, bitmap, HyperLogLog, geo, script, and server facets;
  an audited [command catalog and raw/interpolated execution](guides/raw-commands.md), plus
  [source-generated custom commands](guides/generated-commands.md).
- [Typed LCS index matches](commands/strings-and-keys.md#longest-common-subsequence), with
  inclusive byte ranges, match-length filtering, and batch/transaction parity.
- [Blocking queues](guides/blocking-queues.md) and [streamed string transfers](commands/strings-and-keys.md)
  use dedicated connections where needed to keep multiplexed traffic moving.
- [Hosted stream consumers](guides/stream-workers.md) support bounded concurrency, scoped typed handlers,
  explicit acknowledgement and graceful draining. Automatic idle recovery, delivery limits,
  dead-letter handling and worker telemetry remain tracked by [#891](https://github.com/thomhurst/Respire/issues/891).
- [Batches, transactions, and `WATCH`](guides/batches-and-transactions.md), plus
  [durability acknowledgements](guides/durability-acknowledgements.md).
- [Pub/sub](guides/pub-sub.md), pattern and sharded subscriptions, and delivery-gap reporting.
  Typed [keyspace, keyevent, and Redis 8.8 subkey notifications](guides/keyspace-notifications.md)
  include Cluster routing to the owning primary for exact keys and fan-out across primaries for patterns.
  Reconnects can lose notifications; topology changes can lose or duplicate them.
- [Managed distributed locks](guides/distributed-locks.md) and [coordination](guides/coordination.md)
  with fencing tokens, leases, semaphores, and rate limiting.
- Typed [JSON](guides/json.md), [Search](guides/search.md), [TimeSeries](guides/timeseries.md),
  [probabilistic](guides/probabilistic.md), and [vector-set](guides/vector-sets.md) APIs.
- [Generated JSON mappers](guides/generated-json-mappers.md) with reflection-free scalar codecs,
  model key templates, and typed RedisJSON document and property operations.
- [Generated Search schemas](guides/generated-search-schemas.md) for mapped hash and JSON properties,
  with vector codecs, compile-time diagnostics and a public VectorStore metadata seam.

### Caching, integration, and testing

- Bounded RESP3 [server-assisted client-side caching](fundamentals/client-side-caching.md),
  with OPTIN or BCAST/prefix tracking for eligible reads.
- Typed serialization and custom serializers, with optional [value compression codecs](guides/value-codecs.md).
- [Dependency injection](integrations/dependency-injection.md),
  [Microsoft distributed caching and `HybridCache`](integrations/caching.md), including opt-in L1 key tracking
  and cross-instance tag invalidation, and
  [OpenTelemetry traces and metrics](integrations/observability.md).
- A [Prometheus export and pinned Redis dashboard smoke check](integrations/observability.md#prometheus-and-the-published-redis-dashboard),
  with default/optional group validation and documented panel adaptations and unavailable measurements.
- An [in-memory testing server](guides/in-memory-testing.md) with controlled expiry and faults,
  [Redis/Valkey container fixtures](guides/testing-containers.md), and a
  [shared test sample](guides/testing-sample.md) for .NET 8 and .NET 10.
- A [.NET Aspire AppHost and HTTP sample](integrations/aspire-sample.md) using the public
  client integration, Redis/Valkey references, cache backends, health checks, and dashboard telemetry.

## Planned work

The [StackExchange.Redis migration boundary](./guides/stackexchange-interop)
provides binary-safe value conversion, a native raw-command bridge, and the limited
multiplexer/database/batch adapter required by the official distributed cache and
DataProtection packages. It also supports the hash/list command facet inventoried
for Hangfire.Redis.StackExchange, including deferred batch reads and writes.
The adapter also provides endpoint discovery, physical server INFO/TIME/ROLE,
atomic token locks, and literal callback subscriptions used by RedisStorage.
General StackExchange.Redis interface parity and SignalR/Hangfire
acceptance remain planned under [#889](https://github.com/thomhurst/Respire/issues/889).

These open epics track remaining work, not release commitments. Follow their linked issues for
acceptance criteria, dependencies, and current status:

- [Resilience and API parity](https://github.com/thomhurst/Respire/issues/857): command retry policies,
  circuit breaker integration for topology routing, and further connection/API work.
- [Error telemetry ownership hardening](https://github.com/thomhurst/Respire/issues/1046): strengthen
  stale-observation checks, consolidate reporting ownership, and independently audit command coverage.
- [Typed command coverage](https://github.com/thomhurst/Respire/issues/858): Redis 8.10 / Valkey 9.1
  commands, missing options, and module/admin APIs.
- [Ecosystem integrations](https://github.com/thomhurst/Respire/issues/859): Aspire, ASP.NET Core,
  messaging, caching, and other libraries that currently depend on StackExchange.Redis.
- [Higher-level capabilities](https://github.com/thomhurst/Respire/issues/860): stream workers,
  source-generated object mapping and field-level caching research.
  [Generated hash codecs](./guides/generated-hash-codecs) provide scalar model conversion, key templates,
  Redis hash writes/full reads, explicit partial reads, field TTL and change tracking. The remaining
  [object mapper work](https://github.com/thomhurst/Respire/issues/895) includes
  final AOT/performance acceptance.
- [Documentation and samples](https://github.com/thomhurst/Respire/issues/861): guides and runnable
  examples for the expanded feature set.

See [Coming from StackExchange.Redis](./stackexchange-redis) for a feature comparison.

## Design source

The guides linked above describe the full surface and its tradeoffs. The [introduction](./intro.md#design-bets) explains the product bets and where the client fits.

Track changes and contribute through [GitHub issues](https://github.com/thomhurst/Respire/issues).
