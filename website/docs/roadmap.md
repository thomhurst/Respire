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
- [Independent liveness bounds](fundamentals/connections.md#independent-liveness-bounds) for
  receive progress and borrowed large-SET writes, with compatible defaults and safe input reuse.
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
  and health-checked [failover groups](guides/failover-groups.md) across deployments, with
  member-local maintenance handoffs and bounded pauses in health failure counting.
- [Opt-in endpoint circuit breakers](guides/circuit-breakers.md) reject standalone, Sentinel and Cluster
  immediate, batch, and transaction commands during endpoint failures, with independent data-endpoint
  histories, bounded half-open probes, and transition/rejection metrics. They
  [compose with command retries](guides/circuit-breakers.md#retries): an open endpoint is never retried,
  while a retry that moves to another endpoint acquires fresh admission there.
- [Renewable credentials](guides/renewable-credentials.md), independent Sentinel authentication,
  and [Azure Managed Redis](guides/azure-managed-redis.md) / [AWS IAM](guides/aws-iam-credentials.md) adapters.

### Commands and coordination

- Typed string, key, collection, stream, bitmap, HyperLogLog, geo, script, and server facets;
  an audited [command catalog and raw/interpolated execution](guides/raw-commands.md), plus
  [source-generated custom commands](guides/generated-commands.md).
- [Typed Redis 8.10 / Valkey 9.1 command coverage](guides/raw-commands.md#finding-typed-apis),
  including [sparse arrays](commands/arrays.md), bounded counters, multi-element list moves,
  set cardinalities, [hash import sessions](guides/hash-import.md), stream configuration and
  consumer recovery options. Availability depends on the serving server and command version.
- [Explicit-node lifecycle and Cluster administration](guides/server-node-lifecycle.md),
  [typed Sentinel administration](guides/sentinel-administration.md), and
  [client filters](guides/client-administration.md#filtering-and-closing-clients).
- [Typed LCS index matches](commands/strings-and-keys.md#longest-common-subsequence), with
  inclusive byte ranges, match-length filtering, and batch/transaction parity.
- [Blocking queues](guides/blocking-queues.md) and [streamed string transfers](commands/strings-and-keys.md)
  use dedicated connections where needed to keep multiplexed traffic moving.
- [Hosted stream consumers](guides/stream-workers.md) support bounded concurrency, scoped typed handlers,
  explicit attempt-fenced acknowledgement, automatic idle recovery, delivery limits,
  atomic dead-letter completion, graceful draining, capability-aware `CLAIM`/`XNACK`/`XACKDEL`,
  bounded group metrics and W3C trace propagation.
- [Idempotent stream appends](guides/command-retries.md#idempotent-stream-appends): retry-enabled
  typed `XADD` calls attach a per-client producer identity on Redis 8.6+, so a resend after a
  lost reply cannot duplicate the entry.
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
- [Generated hash codecs](guides/generated-hash-codecs.md) with scalar model conversion, key templates,
  Redis hash writes/full reads, explicit partial reads, field TTL and change tracking.
  The [Native AOT sample](guides/generated-hash-codecs.md#native-aot-conformance-sample) covers
  hash, JSON and Search mapping on .NET 8 and .NET 10; mapper comparisons measure generated
  operations against bracketed handwritten controls on .NET 10.
- [Generated Search schemas](guides/generated-search-schemas.md) for mapped hash and JSON properties,
  with vector codecs, compile-time diagnostics and a public VectorStore metadata seam.

### Caching, integration, and testing

- [Semantic chat response caching](guides/semantic-cache.md) for `Microsoft.Extensions.AI`,
  with exact user/model/system/history scopes, cosine thresholds, atomic entry TTL,
  request opt-out and explicit streaming bypass.
- [VectorData hash and JSON collections](guides/vector-data.md) with explicit AOT-friendly mapping and KNN search.
  JSON mapping uses generated serializer metadata and supports nested schema paths.
  Both support expression filters, filtered retrieval and capability-aware
  FT.HYBRID search on Redis 8.4+. Applicable pinned official hash and JSON model, CRUD, vector,
  distance/index and hybrid contracts run on RESP2 and RESP3; exclusions are listed in the guide.
  Official completeness checks enforce the documented capability exclusions, with NativeAOT smoke on .NET 8 and .NET 10.

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
- An [ASP.NET Core sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.AspNetCore)
  combining dependency injection, distributed caching, `HybridCache`, output caching, health checks,
  and OpenTelemetry. It builds and runs in CI on .NET 8 and .NET 10.
- Runnable [Cluster](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Cluster)
  and [Sentinel](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Sentinel)
  samples with local Docker Compose topologies. CI builds both samples on .NET 8 and .NET 10;
  their [topology smoke workflow](https://github.com/thomhurst/Respire/blob/main/samples/TopologySmoke.md)
  verifies Cluster slot routing and Sentinel primary promotion.

### Compatibility adapters

- The [StackExchange.Redis migration adapter](./guides/stackexchange-interop) (`Respire.StackExchangeCompat`)
  provides binary-safe value conversion, a native raw-command bridge, and the documented subset of
  `IConnectionMultiplexer`, `IDatabase`, `IBatch`, `ITransaction`, `IServer` and `ISubscriber`.
  It runs the upstream Microsoft distributed cache and DataProtection tests (52 cases) and
  [pinned Hangfire acceptance](./guides/hangfire-acceptance) (93 upstream facts and two worker
  lifecycle cases) on .NET 8 and .NET 10 with RESP2 and RESP3. It does not provide general
  StackExchange.Redis interface parity.
- Microsoft's SignalR Redis backplane does not run on the adapter
  ([#1268](https://github.com/thomhurst/Respire/issues/1268), not planned). Use the native
  [`Respire.SignalR`](./integrations/signalr) backplane instead.
- The [Orleans 10.2.2 acceptance suite](./guides/orleans-compatibility) runs the official Orleans
  Redis clustering, grain storage and reminder providers on the adapter. Full silo integration,
  Cluster routing and failover remain unvalidated.

### Retries and write outcomes

- [Command retry policies](./guides/command-retries) are opt-in through `RespireOptions.RetryPolicy`.
  Audited per-command retry categories decide what may be resent after a possible execution.
  Retries cover immediate commands on standalone, Sentinel and Cluster clients, raw, script and
  function requests with a caller-declared category, failover-group operations, batches and
  unwatched transactions as complete units, and
  [watched transaction helpers](guides/batches-and-transactions.md#automatic-watched-transaction-retries).
  An enabled policy adds latency and allocation to every eligible command; the guide reports the measurements.
- Internal transport write outcomes record whether a send attempt is definitely unwritten or may
  have reached Redis; the retry policy uses them to decide on a resend. The
  [transport write evidence log](./guides/transport-write-evidence) records their limits and audits.

The [higher-level capabilities epic](https://github.com/thomhurst/Respire/issues/860) is delivered:
stream workers, idempotent stream producers, maintenance-aware failover groups, coherent
HybridCache L1 invalidation and generated object mapping are available above. The
[field-level caching investigation](guides/keyspace-notifications.md#field-level-cache-decision)
concluded that subkey notifications cannot replace `CLIENT TRACKING` under the existing
cache consistency contract. No notification-based coherent field cache is offered.

## Planned work

These open epics track the remaining work before 1.0. They are not release commitments; follow
their linked issues for acceptance criteria and current status.

- [1.0 release gate](https://github.com/thomhurst/Respire/issues/1567): the checks a stable release must pass.
- [API consistency breaks before 1.0](https://github.com/thomhurst/Respire/issues/1576): public
  API changes planned before the API is frozen.
- [Onboarding, docs and developer experience](https://github.com/thomhurst/Respire/issues/1585).
- [High-level features informed by other Redis clients](https://github.com/thomhurst/Respire/issues/1594),
  including a [reliable job queue on `Respire.Streaming`](https://github.com/thomhurst/Respire/issues/1595).

See [Coming from StackExchange.Redis](./stackexchange-redis) for a feature comparison.

## Design source

The guides linked above describe the full surface and its tradeoffs. The [introduction](./intro.md#design-bets) explains the product bets and where the client fits.

Track changes and contribute through [GitHub issues](https://github.com/thomhurst/Respire/issues).
