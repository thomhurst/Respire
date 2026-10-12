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
  histories, bounded half-open probes, and transition/rejection metrics. Remaining resilience integration stays
  tracked by [the circuit breaker issue](https://github.com/thomhurst/Respire/issues/863).
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
  Producer deduplication remains tracked by [#891](https://github.com/thomhurst/Respire/issues/891).
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

## Planned work

The [StackExchange.Redis migration boundary](./guides/stackexchange-interop)
provides binary-safe value conversion, a native raw-command bridge, and the limited
multiplexer/database/batch adapter required by the official distributed cache and
DataProtection packages. It also supports the hash/list/key/string/set/sorted-set
command facets inventoried for Hangfire.Redis.StackExchange, including deferred
batch reads/writes and cursor-based scans. Its transaction adapter supports
deferred MULTI/EXEC, the inventoried Hangfire notification calls, and documented
WATCH conditions, including synchronous Execute and repeat execution after aborts.
The adapter also provides endpoint discovery, physical server INFO/TIME/ROLE,
atomic token locks, and literal callback subscriptions used by RedisStorage.
[Pinned Hangfire acceptance](./guides/hangfire-acceptance) exercises 93 upstream
facts and two real worker lifecycle cases on .NET 8/.NET 10 and RESP2/RESP3,
with explicit exclusions and limitations. The cache/DataProtection inventory now
covers 52 upstream cases across all six applicable test classes, excluding only
the cache's two internal HybridCache detection rows. CI runs both protocols on
both supported frameworks. General StackExchange.Redis interface
parity and combined Microsoft/Hangfire acceptance remain planned under
[#889](https://github.com/thomhurst/Respire/issues/889). Running Microsoft's
SignalR backplane on the adapter is not planned
([#1268](https://github.com/thomhurst/Respire/issues/1268)); the native
[`Respire.SignalR`](./integrations/signalr) backplane covers SignalR scale-out.

The [Orleans 10.2.2 acceptance suite](./guides/orleans-compatibility) covers
clustering membership and gateways, atomic grain persistence, and reminders
through the existing official providers. The adapter now supports their Lua
and lexicographic sorted-set calls, including ETag conflicts, binary state,
expiry, reminder replacement, hash-ring ranges, and storage interoperability.
This delivers [#890](https://github.com/thomhurst/Respire/issues/890) through
the compatibility adapter; separate native providers are unnecessary for these
assessed paths. Full silo integration, Cluster routing, and failover remain unvalidated.

These open epics track remaining work, not release commitments. Follow their linked issues for
acceptance criteria, dependencies, and current status:

- [Resilience and API parity](https://github.com/thomhurst/Respire/issues/857): command retry policies,
  circuit breaker integration for topology routing, and further connection/API work.
  [Command retry policy settings](./guides/command-retries) and conservative safety decisions are
  implemented for buffered immediate commands on standalone, Sentinel and Cluster clients, caller-declared
  raw, script and function requests, failover-group operations, and batches as complete units
  (independently per slot group in Cluster). Watched transaction helpers now
  [replay complete callbacks](guides/batches-and-transactions.md#automatic-watched-transaction-retries)
  for permitted transient failures, with independent WATCH-conflict and transient budgets. Unwatched
  standalone, Sentinel and Cluster transactions retry as complete units. The
  [whole-unit acceptance audit](./guides/command-retries#whole-unit-acceptance) keeps
  [#1216](https://github.com/thomhurst/Respire/issues/1216) open: real Redis and lifetime coverage
  ([#1537](https://github.com/thomhurst/Respire/issues/1537)), a Cluster replica cursor-affinity defect
  ([#1536](https://github.com/thomhurst/Respire/issues/1536)) and the healthy-path unit comparisons are
  unmet. The full circuit composition remains in [#1256](https://github.com/thomhurst/Respire/issues/1256).
  [#1499](https://github.com/thomhurst/Respire/issues/1499) and
  [#1215](https://github.com/thomhurst/Respire/issues/1215) are closed, but their performance gaps below
  are still unmet. Public fault tests include discovery,
  backoff cancellation, deadlines, error ownership, tracked Cluster cache producers and pooled reuse.
  Real Redis tests check increment effects after Sentinel promotion, group switching and Cluster slot migration.
  The [completed policy comparison](./guides/command-retries#completed-policy-comparison) and
  [owner/historical-default comparisons](./guides/command-retries#completed-owner-and-historical-default-comparisons)
  disclose intervals, allocations, drift and retention. Default/disabled integer paths report zero allocation;
  completed enabled measurements allocate. The historical Cluster latency signal remains unattributed.
  Pooled retry frames reduce isolated snapshot allocations; completed end-to-end follow-up assessment
  and tracked Cluster cache overhead remain unmet. Complete reclamation and every lifetime race are not proven.
  The retry epic [#862](https://github.com/thomhurst/Respire/issues/862) remains open.
  No zero-overhead claim is made.
  [Transport write outcomes](./guides/transport-write-evidence) are implemented.
  [#1214](https://github.com/thomhurst/Respire/issues/1214) and
  [#1474](https://github.com/thomhurst/Respire/issues/1474) are closed. Their final audit left
  aggregate transport performance acceptance unmet, and that result still counts against #862's
  healthy-path criterion.
  [Idempotent stream appends](./guides/command-retries#idempotent-stream-appends)
  ([#892](https://github.com/thomhurst/Respire/issues/892)) are implemented: retry-enabled typed
  `XADD` calls attach a per-client producer and per-call `IDMP` identity on Redis 8.6+, so a resend
  after a lost reply leaves one entry. Older servers fall back to ordinary `XADD`.
- [Error telemetry ownership hardening](https://github.com/thomhurst/Respire/issues/1046): strengthen
  stale-observation checks, consolidate reporting ownership, and independently audit command coverage.
- [Ecosystem integrations](https://github.com/thomhurst/Respire/issues/859): Aspire, ASP.NET Core,
  messaging, caching, and other libraries that currently depend on StackExchange.Redis.
- [Higher-level capabilities](https://github.com/thomhurst/Respire/issues/860): stream workers,
  source-generated object mapping and field-level caching research.
  [Stream workers](./guides/stream-workers) include serving-server capability discovery for
  CLAIM recovery, delayed fenced XNACK release, and optional XACKDEL ACKED completion.
  [Generated hash codecs](./guides/generated-hash-codecs) provide scalar model conversion, key templates,
  Redis hash writes/full reads, explicit partial reads, field TTL and change tracking. The remaining
  [object mapper work](https://github.com/thomhurst/Respire/issues/895) includes
  final AOT/performance acceptance.

See [Coming from StackExchange.Redis](./stackexchange-redis) for a feature comparison.

## Design source

The guides linked above describe the full surface and its tradeoffs. The [introduction](./intro.md#design-bets) explains the product bets and where the client fits.

Track changes and contribute through [GitHub issues](https://github.com/thomhurst/Respire/issues).
