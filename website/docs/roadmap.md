---
title: Status and roadmap
description: What Respire supports today and where further work is tracked.
---

# Status and roadmap

The features below are implemented; planned work and validation gaps are listed separately.
Each linked guide lists its server and protocol requirements.

The next supported major targets **2.0.0** to avoid the occupied NuGet 1.x line.
The pipeline supports manually published [release candidates](getting-started.md#release-candidates)
before the stable cut; ordinary pushes only validate packages.

Six settling surfaces now require an explicit [experimental API opt-in](experimental-apis.md):
VectorData, semantic chat caching, hedged reads, hash import sessions, deferred raw commands,
and StackExchange.Redis compatibility. These remain outside the 2.0 compatibility promise.

Public interface compatibility and the post-2.0 extension policy are described in
[Interface compatibility](intro.md#interface-compatibility).

## Available now

- [Polygon geo searches](commands/geo.md#polygon-searches-valkey-90) on Valkey 9.0+,
  including search/store, batch and transaction APIs, and client-side vertex validation.

- Opt-in [maximum connection lifetime](fundamentals/connections.md#maximum-connection-lifetime)
  with independent jitter, replacement-before-drain multiplexing, and idle-only dedicated recycling.

The previous release-gate, API, onboarding, and feature epics are complete
([#1567](https://github.com/thomhurst/Respire/issues/1567),
[#1576](https://github.com/thomhurst/Respire/issues/1576),
[#1585](https://github.com/thomhurst/Respire/issues/1585), and
[#1594](https://github.com/thomhurst/Respire/issues/1594)). Their implemented features
are described in the guides below; the remaining pre-release work is tracked separately.

- [Consistent native public type names](fundamentals/api-naming.md): `Respire` prefixes,
  `IRespire` interfaces, and `Result` suffixes for command outcome wrappers.

- [Local cross-slot diagnostics](fundamentals/connections.md#client-side-cross-slot-validation)
  distinguish client validation from Redis replies, with slot details and opt-in key context.
- Opt-in [cross-slot multi-key splitting](fundamentals/connections.md#splitting-cross-slot-commands)
  for MGET, MSET, DEL, UNLINK, EXISTS, and TOUCH, with per-slot recovery and ordered results.

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

- A [timeouts and thread-pool starvation guide](guides/timeouts.md) explains timeout
  snapshots, scheduling diagnostics, and StackExchange.Redis v3 message equivalents.
- [Physical connection hooks](guides/connection-hooks.md) for auditing, timing, and rejection,
  including reconnects, dedicated connections, Cluster, replicas, and Sentinel.
- [Custom connection streams](fundamentals/connections.md#custom-connection-streams) through a public factory,
  with owned disposal, TLS, reconnects, discovery, pub/sub, and dedicated connections.
- [Dedicated pool sizing](fundamentals/connections.md#dedicated-connection-pools) with opt-in
  total limits, cancellation-aware acquisition, timed idle retirement and pool metrics.
- Opt-in [blocking cancellation with CLIENT UNBLOCK](guides/blocking-queues.md#cancellation)
  reuses dedicated connections after draining the reply, with bounded discard fallback.
- [Connections](fundamentals/connections.md) from Redis URIs, pasted Azure portal strings, StackExchange.Redis connection
  strings, or `RespireOptions`, with lazy or eager connect, TLS, and automatic pipelining.
- [Independent liveness bounds](fundamentals/connections.md#independent-liveness-bounds) for
  receive progress and large borrowed `SET` writes.
- [Exception endpoint context](fundamentals/connections.md#exception-endpoint-context) through
  the common exception base, populated by physical connection and reply failures, deferred
  batch/transaction conversions, timeout snapshots, and endpoint-aware wrappers; routing
  failures without a known single endpoint may still report an unknown endpoint.
- [RESP3 preference](fundamentals/connections.md#protocol-negotiation) with RESP2 fallback and
  explicit protocol overrides.
- [Redis Cluster](fundamentals/connections.md#redis-cluster-endpoint-identity) routing with
  topology refresh, redirect recovery, and same-slot transactions.
- [Automatic Sentinel failover](fundamentals/connections.md#redis-sentinel), validated with `ROLE`,
  plus recovery after disconnects or `READONLY` replies.
- [Node address mapping](guides/node-address-mapping.md) for NAT, Docker published ports, and Kubernetes routes,
  with static maps, callbacks, and control over the TLS hostname.
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
  and [Azure Managed Redis](guides/azure-managed-redis.md), [AWS IAM](guides/aws-iam-credentials.md),
  and [Google Cloud Memorystore](guides/google-cloud-memorystore.md) adapters.

### Commands and coordination

- [Idempotency claims and binary result replay](guides/coordination.md#idempotency-claims-and-result-replay)
  provide expiring ownership, request fingerprints, and atomic completion/abandon.
  [HTTP idempotency middleware](integrations/idempotency.md) adds caller-scoped response replay
  and bounded request/response buffering for ASP.NET Core endpoints.

- Bundled warnings for [short-lived clients](diagnostics/RESP010.md),
  [unbounded collection reads](diagnostics/RESP011.md), and
  [blocking subscription consumers](diagnostics/RESP012.md).

- [Async-only native APIs](guides/async-only.md), with the bundled RESP009 warning
  and an `await` code fix for synchronous consumption of Respire tasks.

- GC-owned raw, script, and function results by default, with explicit
  [pooled result leases](guides/raw-commands.md#result-lifetime) and a shared
  `await using` pattern for batches and transactions.
- [Queued result diagnostics](guides/batches-and-transactions.md#ignoring-queued-results):
  CS4014 suppression for ignored pending results, with RESP002 checks for premature reads and awaits.

- Typed facets for strings, keys, collections, streams, [bitmaps](commands/bitmaps.md),
  [HyperLogLog](commands/hyperloglog.md), [geo](commands/geo.md), [scripts](guides/scripting.md),
  [functions](commands/functions.md), and
  server commands; a [command catalog with raw and interpolated execution](guides/raw-commands.md);
  and [source-generated custom commands](guides/generated-commands.md).
- [Typed Redis 8.10 / Valkey 9.2 RC commands](guides/raw-commands.md#finding-typed-apis), including
  [sparse arrays](commands/arrays.md) and [hash import sessions](guides/hash-import.md).
  Availability depends on the server version.
- [Conditional transactions](guides/batches-and-transactions.md#conditional-transactions-valkey-92)
  with Valkey 9.2+ EXEC IFEQ, IFNE, NX, and XX preconditions.
- [Explicit-node lifecycle and Cluster administration](guides/server-node-lifecycle.md),
  [Sentinel administration](guides/sentinel-administration.md), and
  [client filters](guides/client-administration.md#filtering-and-closing-clients).
- [Typed LCS matches](commands/strings-and-keys.md#longest-common-subsequence).
- [Blocking queues](guides/blocking-queues.md) and [streamed string transfers](commands/strings-and-keys.md)
  on dedicated connections.
- [Typed job queues](guides/job-queues.md) with transactional enqueue, delayed scheduling, batch priority, TTL, durable
  retry backoff, bounded enqueue deduplication, distributed round-robin job groups, scoped handlers, visibility recovery, dead letters,
  queue-wide pause/resume and shared fixed-window admission limits, per-group concurrency leases,
  GCRA rate limits and pause/resume,
  fenced progress and bounded per-job logs, retained completion events, enqueue-to-processing trace links, nested
  parent/child flows with retained results and failure policies, flat batches with atomic
  success/failure/completion callback publication, progress counts, cooperative cancellation and dashboard monitoring, and durable
  UTC cron/fixed-interval schedules with cross-instance tick deduplication and missed-tick policies.
- [Reliable topics](guides/reliable-topics.md) with durable named subscriptions, competing consumers,
  acknowledgement leases, delayed rejection, per-subscription dead letters, retention and replay.
- [Reliable message queues](guides/reliable-queues.md) with per-delivery visibility timeouts,
  receipt-fenced ACK, delayed NACK, delivery limits, dead letters and optional send deduplication.
- [Hosted stream consumers](guides/stream-workers.md) with bounded concurrency, scoped typed
  handlers, idle recovery, delivery limits, dead-lettering, graceful draining, metrics, and
  W3C trace propagation.
- [Queue and stream dashboard](guides/stream-dashboard.md) for configured streams and job queues,
  with group statistics, pending deliveries, dead letters, queue pause state and limiter hits,
  retained job logs, configured batch progress, bounded completion/failure history charts, paginated application job group
  inspection and pause/resume, and authorized retry/delete actions.
- [Idempotent stream appends](guides/command-retries.md#idempotent-stream-appends): on Redis 8.6+,
  a retried `XADD` cannot duplicate the entry.
- [Batches, transactions, and `WATCH`](guides/batches-and-transactions.md), plus
  [durability acknowledgements](guides/durability-acknowledgements.md).
- [Pub/sub](guides/pub-sub.md) with pattern and sharded subscriptions and delivery-gap reporting.
  [Keyspace, keyevent, and Redis 8.8 subkey notifications](guides/keyspace-notifications.md) work
  on Cluster. Reconnects can lose notifications; topology changes can lose or duplicate them.
- [Distributed locks](guides/distributed-locks.md) and [coordination](guides/coordination.md):
  fencing tokens, [fair FIFO locks](guides/coordination.md#fair-fifo-locks), leases, semaphores, and fixed-window, sliding-window, token-bucket and
  [GCRA rate limiting](guides/coordination.md#gcra), plus
  [atomic composite rate limits](guides/coordination.md#composite-limits) and
  [runtime rate-limit updates](guides/coordination.md#updating-settings-at-runtime), plus
  [deny lists and admission metrics](guides/coordination.md#deny-lists-and-admission-metrics).
- [Hosted leader election](guides/coordination.md#hosted-leader-election) with renewable
  fenced leases, cancellation on revocation, callbacks, events, and transition metrics.
- Typed [JSON](guides/json.md), [Search](guides/search.md), [TimeSeries](guides/timeseries.md),
  [probabilistic](guides/probabilistic.md), and [vector-set](guides/vector-sets.md) APIs.
- Source-generated [JSON mappers](guides/generated-json-mappers.md),
  [hash codecs](guides/generated-hash-codecs.md), and
  [Search schemas](guides/generated-search-schemas.md), with a
  [Native AOT sample](guides/generated-hash-codecs.md#native-aot-conformance-sample) for .NET 8 and .NET 10.
- [LINQ-style generated Search queries](guides/generated-search-schemas.md#typed-queries) for hash and JSON
  models: filters, sorting, paging, field selection, aggregation, and filtered KNN without reflection.
  Selectors use generated fields rather than an `IQueryable` provider.

### Caching, integration, and testing

- [ASP.NET Core rate limiting](integrations/rate-limiting.md) with named policies, user/IP/API-key partitions,
  shared Redis budgets, and `Retry-After` rejection headers.
- [Typed near-cache maps](guides/near-cache-map.md) with read-through loaders, write-through
  or batched write-behind persistence, preload, shared invalidation, and hash field expiry.
- [Semantic chat response caching](guides/semantic-cache.md) for `Microsoft.Extensions.AI`.
- [Redis conversation history](guides/chat-history.md), chat middleware, and Microsoft Agent Framework history providers.
- [Exact-text embedding caching](guides/embedding-cache.md) with batched misses, optional TTL, and hit/miss metrics.
- [VectorData hash and JSON collections](guides/vector-data.md) with AOT-friendly mapping, KNN,
  filters, and hybrid search on Redis 8.4+. The guide lists unsupported features.
- RESP3 [server-assisted client-side caching](fundamentals/client-side-caching.md) with OPTIN,
  OPTOUT, BCAST/prefix tracking, and optional NOLOOP.
- [Cache-aside factories](fundamentals/client-side-caching.md#populate-missing-values-with-a-factory)
  with relative or absolute server expiry and optional concurrent-miss coalescing.
- Typed serialization, custom serializers, and [value compression codecs](guides/value-codecs.md).
- Opt-in [client-side payload encryption](guides/payload-encryption.md) for cache values,
  near-cache maps, job arguments, and stream payloads, using ASP.NET Core Data Protection.
- [Dependency injection](integrations/dependency-injection.md),
  [distributed caching and `HybridCache`](integrations/caching.md) with cross-instance tag
  invalidation, and [OpenTelemetry](integrations/observability.md), including
  [command trace filters](integrations/observability.md#command-trace-filters) and a
  [Prometheus and Redis dashboard setup](integrations/observability.md#prometheus-and-the-published-redis-dashboard).
- [Explicit pipeline and transaction hooks](guides/batches-and-transactions.md#observe-or-reject-explicit-executions)
  for admission rejection, auditing, and completion timing. [Logical command hooks](guides/command-hooks.md)
  cover ordinary converted dispatch, single-key and multi-key cache reads, partial hash reads,
  cache-aside factories, distributed-cache operations, interpolated and non-interpolated Execute APIs,
  function execution and mutations, script execution, dedicated blocking converted replies, ordinary cursor pages,
  streaming upload/download results, explicit subscription setup/teardown,
  typed TimeSeries results including chunked `TS.MADD` partial-progress failures,
  specialized server/pinned/fan-out operations, resumable cluster scan pages,
  stream appends including automatic IDMP retries and capability fallback,
  explicit batch/transaction members (including WAIT/WAITAOF), WATCH setup, immediate hash imports,
  import-session acquisition, explicit health-probe rounds, managed locks, semaphores,
  tracked coordination leases and countdown latch operations (including notification waits),
  and typed Search results (including hybrid capability discovery and cursor-page recovery).
  Typed RedisJSON owners include serialization, conversion and owned cleanup. Typed
  Probabilistic owners include argument preparation, conversion and owned cleanup, with
  raw Info and Top-K List transfers retaining their own boundaries. Rate limiters and Redlock
  groups admit their logical operations before internal work. The completed
  [policy audit](guides/command-hooks.md#policy-boundaries-and-decorators) documents custom
  facet cancellation, decorator/result boundaries, housekeeping exclusions and recorded
  benchmark evidence. Hooks describe supported logical operations; they are not a universal
  wire-command or arbitrary-wrapper authorization policy.
- [Configuration binding and named options](integrations/dependency-injection.md#bind-appsettingsjson-and-validate-at-startup),
  with a generated builder covering every client option and validation at host startup.
- An [in-memory test server](guides/in-memory-testing.md) with controlled server time, `KEYS`/`SCAN` enumeration,
  and [command expectations](guides/in-memory-testing.md#command-expectations) with argument matching, reply overrides,
  and invocation verification; [Redis/Valkey container fixtures](guides/testing-containers.md),
  and a [test sample](guides/testing-sample.md).
- A focused [Garnet, Dragonfly and KeyDB compatibility suite](guides/server-compatibility.md)
  runs weekly or on request, with pinned server versions and explicit incompatibility skips.
  Its feature matrix describes the assessed scope rather than full Redis parity.
- Samples: [.NET Aspire](integrations/aspire-sample.md),
  [ASP.NET Core](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.AspNetCore),
  [Cluster](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Cluster), and
  [Sentinel](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Sentinel), with a
  [topology smoke workflow](https://github.com/thomhurst/Respire/blob/main/samples/TopologySmoke.md).

### Compatibility adapters

- A [coordination migration guide](./guides/coordination-migration.md) maps RedLock.net and
  DistributedLock.Redis to Respire, including quorum, renewal, timeout, and fencing differences.

- An opt-in [StackExchange.Redis migration analyzer and code fix](./guides/stackexchange-migration-tool)
  converts supported static async `IDatabase` methods to native Respire calls, with semantic
  diagnostics, compilation checks, and Fix All. Connection setup and unsupported shapes require
  manual migration.

- The [StackExchange.Redis migration adapter](./guides/stackexchange-interop) (`Respire.StackExchangeCompat`)
  covers a documented subset of `IConnectionMultiplexer`, `IDatabase`, `IBatch`, `ITransaction`,
  `IServer`, and `ISubscriber`, plus value conversion and a raw-command bridge. It passes the
  upstream Microsoft distributed cache and DataProtection tests and
  [Hangfire acceptance](./guides/hangfire-acceptance). It is not full StackExchange.Redis parity.
- Microsoft's SignalR Redis backplane does not run on the adapter, and support is not planned. Use
  [`Respire.SignalR`](./integrations/signalr).
- The [Orleans 10.2.2 acceptance suite](./guides/orleans-compatibility) runs the Orleans Redis
  clustering, grain storage, and reminder providers on the adapter. Real silo/client scenarios cover
  activation, persistent state, reminder delivery, graceful restart, shared and provider-owned shutdown,
  stable three-primary Cluster routing, completed Cluster slot moves, coordinated replica promotion,
  abrupt silo death, socket reconnect, and Sentinel primary promotion on .NET 8/10 with RESP2/RESP3.
  Membership inserts, updates, stale-version rejection and heartbeats during ASK migration are covered; unplanned Cluster failover and
  multi-host partitions remain unvalidated.

### Retries and write outcomes

- A bounded [standalone, Sentinel and Cluster reconnect backlog](./guides/reconnect-policy.md#standalone-command-backlog)
  waits for replacement command connections by default, including typed/raw commands,
  fire-and-forget, batches and unwatched transactions. It preserves FIFO admission, original
  deadlines and cancellation, with `BacklogCapacity` and a `FailFast` opt-out. Accepted commands
  are not replayed by the backlog. Sentinel primary recovery uses a router-owned FIFO that
  survives generation replacement and validates the elected primary before dispatch.
  Cluster queues are independent per slot and reselect owners and read policies during recovery.
  Standalone and Sentinel replica reads have a shared router FIFO that reapplies the read policy,
  membership and ROLE validation after recovery, including procedures, streams and hedged reads.
- Opt-in [command retry policies](./guides/command-retries) through `RespireOptions.RetryPolicy`.
  Each command has a retry category that decides whether it may be resent after it might have run.
  Retries cover immediate commands, raw/script/function requests with a declared category,
  failover groups, whole batches and unwatched transactions, and
  [watched transaction helpers](guides/batches-and-transactions.md#automatic-watched-transaction-retries).
  An enabled policy adds retry ownership work and can allocate on healthy commands.
  The [retry-owner lifetime audit](guides/command-retries.md#lifetime-validation) maps
  cancellation, replacement, late-reply and pool-generation coverage, including synchronized
  public-operation tests and quiescent reference checks. It does not establish complete heap
  reclamation or exhaust every scheduler/topology combination.
- The [transport write evidence log](./guides/transport-write-evidence) records how Respire tells
  an unsent command from one that may have reached Redis.

The [14 October healthy-path audit](guides/command-retries.md#healthy-path-acceptance-audit-14-october-2026)
completes the evidence disposition in [#1693](https://github.com/thomhurst/Respire/issues/1693).
The original zero-allocation/no-measurable-cost goal remains unmet. Completed owner comparisons
report 0 B/op for Default/Disabled integer reads, positive Enabled allocations including tracked
Cluster cache producers, and mixed latency/control drift. Transport's
[current disposition](guides/transport-write-evidence.md#healthy-path-disposition-14-october-2026)
also remains negative. The guides define per-path targets, known causes and the evidence needed
before further optimization; closed #862, #1214 and #1499 are historical predecessors, not
open blockers. These historical results do not certify current main or promise zero overhead.

A notification-based field cache is not offered: subkey notifications cannot meet the cache
consistency contract. See the [field-level caching decision](guides/keyspace-notifications.md#field-level-cache-decision).

## Planned work

The current pre-release epics target **2.0.0**, as recorded in the
[release naming decision](https://github.com/thomhurst/Respire/issues/1796).
They track remaining work, not release commitments.

- [Release hygiene before 2.0](https://github.com/thomhurst/Respire/issues/1815):
  release naming, package compatibility validation, and the remaining Orleans and
  Cluster replica recovery checks.
- [Remaining feature gaps against other Redis clients](https://github.com/thomhurst/Respire/issues/1816):
  job groups and queue controls,
  client-side encryption, coordination APIs, and testing support.
  [MessagePack, protobuf-net, and MemoryPack serializers](fundamentals/values-and-serialization.md#binary-serializer-packages) are available as optional packages.
  The existing [job queue](guides/job-queues.md), [dashboard](guides/stream-dashboard.md),
  [typed queries](guides/generated-search-schemas.md#typed-queries), and
  [near-cache map](guides/near-cache-map.md) are already available.

## Validation and performance follow-ups

- [Published StackExchange.Redis 3.4.5 and 4.0.127-alpha comparisons](./performance#measured-comparisons-october-2026):
  Linux measurements and durable raw evidence cover ordinary operations, pipelines, pub/sub,
  and local caches. The [server-operation latency investigation](./performance#server-latency-investigation-outcome)
  dispositions all 21 original ties and both controls, retaining 16 ties in the repeat and
  documenting why the profile supports no production optimization. The pipelined control
  changes from `Faster` to `Same`; concurrent GET remains `Faster`.
  [Pub/sub profiling](https://github.com/thomhurst/Respire/issues/1745) found no safe latency
  improvement supported by the collected evidence: the latest runs tie both versions, and
  the earlier 4.0 slowdown remains valid for its original rig. Publication does not establish
  a universal performance advantage.
- [Completed healthy-path retry and transport performance audit](https://github.com/thomhurst/Respire/issues/1693):
  enabled retries allocate and add work even when commands succeed. The audit documents
  remaining acceptance gaps and targets for future optimization; its closure does not
  establish universal performance acceptance.
- [Completed retry-owner lifetime audit](guides/command-retries.md#lifetime-validation)
  ([#1694](https://github.com/thomhurst/Respire/issues/1694)) maps cancellation, disposal,
  replacement, late-reply, and reuse coverage. Its bounded tests and quiescent reference
  checks do not prove complete heap reclamation or every scheduler/topology combination.
- [Orleans topology assessment](https://github.com/thomhurst/Respire/issues/1799):
  lifecycle, provider-owned shutdown, completed Cluster slot moves, coordinated replica promotion,
  Sentinel failover and abrupt silo death have acceptance coverage. Membership writes during
  ASK migration are covered; multi-host partitions need an independent-network test harness.
- [Cluster availability-zone replica fallback timeout](https://github.com/thomhurst/Respire/issues/1749):
  investigate the failure under concurrent test load and validate recovery.

See [Coming from StackExchange.Redis](./stackexchange-redis) for a feature comparison and the
[introduction](./intro.md#design-bets) for the design bets. Report issues on
[GitHub](https://github.com/thomhurst/Respire/issues).
