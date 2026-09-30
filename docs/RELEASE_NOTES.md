# Release notes

## Unreleased

### Optional Zstandard value codec

- `Respire.Compression.Zstd` adds `ZstdValueCodec` with bounded, owned output through
  the shared codec and serializer contracts. Version 1 frames use reserved algorithm
  ID 4 for a single Zstandard frame. The optional managed dependency needs no native
  zstd deployment; per-call contexts are disposed before return. See
  [value codecs](../website/docs/guides/value-codecs.md#optional-zstandard-package).

### Optional LZ4 value codec

- `Respire.Compression.Lz4` adds `Lz4ValueCodec` through the existing value-codec and
  serializer contracts. Version 1 frames use reserved algorithm ID 3 for raw LZ4 blocks,
  with shared thresholds, owned output, checksums, and bounded decompression. The
  K4os dependency is confined to the optional package. See
  [value codecs](../website/docs/guides/value-codecs.md#optional-lz4-package).

### Cluster retirement diagnostics

- `RespireClient.GetClusterRetirementSnapshot()` exposes owned aggregate counts for
  retained generations, oldest retirement age, pending correction fences, unfinished
  transport drains, cleanup failures, and dedicated operation-pool leases/acquisitions.
  Capture performs no network I/O and retains no transport references. See
  [observability](../website/docs/integrations/observability.md#cluster-retirement)
  for concurrency, disposal, and counter scope. Retry and retention policies are unchanged.

### Sentinel discovery validation

- Sentinel-discovered data connections must confirm a valid primary `ROLE` before
  `ConnectAsync` returns. Data-node ACLs need `ROLE` permission; stale replicas and
  invalid candidates are disposed before another Sentinel is tried. Optional
  `SENTINEL SENTINELS` discovery adds bounded, deduplicated fallback peers while
  retaining configured endpoints and Sentinel-specific TLS/authentication settings.
  This is connection-time validation; automatic runtime failover remains planned.

### Command reconnect policy

- Optional `RespireOptions.ReconnectPolicy` configures exponential delay, jitter, a delay cap,
  and maximum attempts for failed command-connection slots, including Cluster nodes.
  Null preserves existing recovery. Lifecycle events carry source slot, attempt, scheduled
  delay, and exhaustion metadata; configured attempt events are published even when aggregate endpoint
  health is unchanged. Histograms record attempts and delay, and a counter records exhausted episodes. No accepted command
  is replayed. Dedicated, pub/sub, and discovery integration remain separate #401 children.
  See the [reconnect guide](../website/docs/guides/reconnect-policy.md).

### Hot-key tracking

- Redis 8.6+ HOTKEYS APIs add a pinned server handle for START/GET/STOP/RESET and
  explicit per-node operations. Owned snapshots preserve binary keys, measurement
  units, optional metrics, and future fields. State changes require `AllowAdmin`.
  Five new `IServerCommands` members are a breaking interface extension for external
  implementers. See the [hot-key guide](../website/docs/guides/hot-keys.md) for shared
  session ownership, sampling, resource costs, and cancellation behavior.

### Opt-in value codecs

- `IRespireValueCodec`, the shared framed-codec base, and built-in Brotli/Deflate codecs
  provide owned mixed compressed/uncompressed values with bounded decoding.
  `RespireValueCodecSerializer` decorates the existing serializer; primitive and raw
  value paths and all default behavior are unchanged. See
  [value codecs](../website/docs/guides/value-codecs.md) for framing, migration, collection
  identity constraints, and explicit raw-byte use. Optional codec packages remain separate
  work; no existing client interface gains members.
- `RespireCacheOptions.ValueCodec` opts distributed-cache and HybridCache L2 payloads into
  the same codec contract. Array and buffer APIs decode transparently; default raw storage,
  expiry metadata, and client ownership are unchanged. Codec-enabled namespaces require
  compatible readers/writers and cannot read unframed legacy entries. See
  [Microsoft caching](../website/docs/integrations/caching.md) for migration and allocation costs.

### Typed vector sets

- `VectorSets` adds VADD/VSIM with direct little-endian FP32 encoding, VALUES support,
  binary members, attributes, metadata, graph links, random/range reads, and deferred
  mirrors. Replies own their storage; batch/transaction inputs are snapshotted when queued.
  Integration contracts are pinned to Redis 8.6.0 on both protocols. **Breaking interface
  addition:** external `IRespireClient` and `IRespireCommandQueue` implementations must
  forward the new property. See the [vector guide](../website/docs/guides/vector-sets.md)
  for accuracy, memory, serialization, and ownership limits. Performance evidence is
  tracked separately in #534; no zero-copy networking or throughput guarantee is implied.

### Proactive thread-pool diagnostics

- A shared background probe now measures thread-pool scheduling delay before command
  timeouts occur. Observable metrics, throttled warnings, and immutable timeout snapshots
  expose the delay and worker counters. Monitoring is enabled by default; set
  `ThreadPoolMonitoring = false` to opt out per client. `ThreadPoolWarningThreshold`
  configures warning sensitivity. Both settings are also available on the DI options
  builder for default and keyed registrations. See
  [observability](../website/docs/integrations/observability.md#thread-pool-scheduling)
  for sampling, lifecycle, units, and interpretation.

### Stream production and negative acknowledgements

- `StreamAddOptions.Idempotency` adds mutually exclusive IDMP/IDMPAUTO production
  with owned binary identifiers (Redis 8.6+). `Streams.NegativeAcknowledgeAsync` and
  deferred `NegativeAcknowledge` expose XNACK modes, retry counts, and FORCE (Redis 8.8+).
  XNACK returns the server's aggregate count; it does not report per-ID outcomes.
  External stream-facet implementations must add four immediate and two deferred overloads. See the
  [stream guide](../website/docs/commands/collections.md#idempotent-production-redis-86)
  for deduplication limits, PEL effects, version requirements, and cancellation.

### Server metadata and persistence

- Server APIs add owned COMMAND INFO/DOCS, binary-safe COMMAND GETKEYS, MODULE LIST,
  CONFIG REWRITE/RESETSTAT, SAVE, BGSAVE, and BGREWRITEAOF, with explicit per-node
  variants. Configuration and persistence require AllowAdmin; background replies
  report acceptance rather than completion. Custom `IServerCommands` implementations
  must add 18 methods as part of the pre-release interface policy. See the
  [metadata and persistence guide](../website/docs/guides/server-metadata-and-persistence.md)
  for server versions, ownership, blocking behavior, and cancellation limitations.

### Server diagnostics

- Server diagnostics add latency and memory doctor reports, typed latency histories and
  cumulative histograms, slow-log lengths, and admin-gated allocator purging. Each has
  an explicit per-node counterpart with endpoint-associated results and errors.
  **Breaking interface change:** custom `IServerCommands` implementations, decorators,
  and mocks must add all twelve methods. See [server diagnostics](../website/docs/guides/server-diagnostics.md) for
  versions, units, ownership, collection costs, and partial-success semantics.

### Valkey command logs

- Server APIs add typed Valkey 8.1+ COMMANDLOG GET/LEN/RESET for slow execution,
  large requests, and large replies. Owned entries retain binary arguments, client
  metadata, measurement units, and future trailing fields. Explicit all-node variants
  preserve endpoint results; RESET requires `AllowAdmin`. This is a breaking interface
  extension for external `IServerCommands` implementations, which must add six members.
  See the [command-log guide](../website/docs/guides/command-logs.md).

### Stream reference policies

- Redis 8.2+ stream removal adds XDELEX and XACKDEL through `RemoveAsync(key, policy, ids)`
  and `AcknowledgeAndRemoveAsync`, with batch/transaction mirrors and typed per-ID outcomes.
  `StreamAddOptions` and `StreamTrimOptions` accept nullable `ReferencePolicy` for KEEPREF,
  DELREF, and ACKED. Null preserves the legacy wire format. Custom stream-facet implementations
  must implement the new members. See [reference-aware removal](../website/docs/commands/collections.md#reference-aware-removal-redis-82)
  for pending-reference semantics and server compatibility.

### Raw Cluster key validation

- Immediate raw execution now rejects cross-slot keys locally for supported layouts, including
  KEYDB.MEXISTS, Redis multi-key commands, script key counts, blocking pops, and stream reads.
  Catalog, string, interpolated, and fire-and-forget forms share the same validation. Standalone
  execution and unknown-layout server validation remain unchanged; requests are never split
  across nodes. See [raw Cluster validation](../website/docs/guides/raw-commands.md#cluster-key-validation)
  for supported layouts and compatibility details.

### Cluster inspection

- Server APIs add owned Cluster info, node, shard, bus-link, and slot-statistics
  models, plus key-slot, local key-count, and node/shard ID queries. Explicit all-node
  variants preserve endpoint-associated results without changing routing or claiming
  a reconciled global topology. Custom `IServerCommands` implementations must add all
  20 methods. This is an intentional pre-release interface extension, consistent with
  the [API design policy](API_DESIGN.md). See the [Cluster inspection guide](../website/docs/guides/cluster-inspection.md)
  for versions, prefix semantics, optional metrics, and ownership.

### Stream trimming

- `StreamAddOptions` adds `MinId` and `Limit`. `Streams.TrimAsync` and deferred `Streams.Trim`
  accept `StreamTrimOptions` for MAXLEN/MINID with exact or approximate trimming. LIMIT requires
  approximate trimming; MINID/LIMIT require Redis 6.2+. Existing MAXLEN overloads and defaults
  are unchanged. Custom stream-facet implementations must add the new trim member. See
  [stream trimming](../website/docs/commands/collections.md#trimming) for validation and input lifetimes.

### Client administration

- `Server.GetClientConnectionAsync` identifies and pins one physical connection for typed
  CLIENT inspection and controls, with explicit endpoint scope for PAUSE/UNPAUSE/UNBLOCK.
  `ClientsOnAllNodesAsync` returns endpoint-associated client lists or failures. Mutations
  require `AllowAdmin`; handles never reconnect or replay settings onto a different socket.
  Custom `IServerCommands` implementations, decorators, and mocks must add both methods.
  See [client administration](../website/docs/guides/client-administration.md) for versions,
  ownership, cancellation, and multiplexed-connection semantics.

### Compatible-server command catalog

- The Dragonfly catalog adds rate limiting, member/field expiry, script and memory
  diagnostics, and Cluster administration descriptors. KeyDB adds millisecond member
  expiry and hash/key extensions. Existing descriptors remain unchanged. See the
  [compatible-server guide](../website/docs/guides/server-extensions.md) for the audited
  sources, exact names, server/version constraints, and protocol-shaped examples.

### Stream reads

- `Streams.ReadAsync` adds owned, typed XREAD results for one or multiple streams, optional
  per-stream COUNT, and optional BLOCK on dedicated connections. `ReadAllAsync` resumes
  transient failures from each stream's last delivered id and resolves initial `$` positions
  once. Batch and transaction `Streams.Read` supports nonblocking reads only.
  External `IStreamCommands` and `IBatchStreamCommands` implementations, decorators, and
  mocks must implement or forward the new members. See the
  [stream reading guide](../website/docs/commands/collections.md#reading-without-consumer-groups)
  for cancellation, ownership, ACL requirements, and trimming/reconnect limitations.

### ACL administration

- Server ACL APIs add owned user, selector, log, and dry-run results, binary-safe
  arguments, and explicit per-node Cluster variants. Mutations require `AllowAdmin`;
  ACL changes remain node-local and can partly succeed across nodes. External
  `IServerCommands` implementations must implement or forward all 18 new methods.
  See the [ACL administration guide](../website/docs/guides/acl-administration.md)
  for Redis versions, missing-user and denial semantics, and result ownership.

### Cluster transport lifecycle

- Successful topology refreshes retire departed and superseded connections and pools, prune
  historical identities, and preserve configured seeds and newer MOVED/ASK routes. Accepted
  commands and borrowed operations drain before cleanup; client disposal still aborts them.
  Correction fences retain their original network peer and TLS identity across topology changes.

### Distributed locks

- Distributed locks use native conditional renewal/deletion on supported Redis and Valkey versions, with per-connection Lua fallback on older servers and preserved managed cancellation fencing.
  Update lock command ACL allowlists for native renewal (`SET`) and release (`DELEX` on Redis 8.4+
  or `DELIFEQ` on Valkey 9.0+). `NOPERM` is surfaced without Lua fallback.

### Pub/sub introspection

- Server pub/sub channel, subscriber, and unique-pattern inspection adds ordinary
  and sharded command forms with owned binary channel results. Explicit
  `OnAllNodesAsync` methods return endpoint-associated values or failures for all
  discovered Cluster members, including replicas. External `IServerCommands`
  implementations, decorators, and mocks must implement or forward all six new methods. See the
  [pub/sub introspection guide](../website/docs/guides/pub-sub-introspection.md).

### Stream metadata

- `Streams.CreateConsumerAsync` adds Redis 6.2+ XGROUP CREATECONSUMER.
  `SetLastIdAsync` exposes Redis 5.0+ XSETID, including Redis 7.0+ ENTRIESADDED and
  MAXDELETEDID metadata. Existing overloads retain their bindings. External
  `IStreamCommands` implementations, decorators, and mocks must implement or forward
  both new members. See the [stream metadata guide](../website/docs/commands/collections.md#stream-metadata)
  for server constraints and the advanced restoration semantics of XSETID.

### Valkey Cluster databases

- `UseCluster` now supports non-zero `Database` values on Valkey 9+ with `cluster-databases`
  configured. Each physical connection checks `INFO SERVER` and completes `SELECT` before use.
  This feature needs INFO/SELECT permissions. Unsupported servers still reject the configuration,
  now during connection setup rather than lazy client construction; database 0 and standalone
  handshakes are unchanged. See the [connection guide](../website/docs/fundamentals/connections.md#non-zero-databases-in-valkey-cluster).

### String comparisons

- `Strings.SetConditionalAsync` and `GetAndSetConditionalAsync` add value/digest
  comparisons without changing existing SET overload bindings. `DeleteConditionalAsync`
  and `DigestAsync` expose Redis 8.4 DELEX/DIGEST; `DeleteIfEqualAsync` exposes Valkey 9.0
  DELIFEQ. SET IFEQ also supports Valkey 8.1. All methods have batch/transaction mirrors.
  External `IStringCommands` and `IBatchStringCommands` implementations, decorators,
  and mocks must implement or forward the new members, including both generic SET forms.
  See [string comparisons](../website/docs/commands/strings-and-keys.md#compare-values-before-writing-or-deleting)
  for server versions, raw comparison operands, buffer ownership, and GET result semantics.

### Deferred raw commands

- Batches, transactions, and watched transactions expose `Execute`, returning an owned
  `RespirePending<RespireResult>`. Known key layouts receive prefixing and Cluster validation;
  unsupported layouts fail before enqueueing. Custom `IRespireCommandQueue` implementations
  must implement the new member. See the [deferred raw guide](../website/docs/guides/deferred-raw-commands.md).

### Durability acknowledgements

- `RespireBatch.ExecuteAndWaitForReplicationAsync` and `ExecuteAndWaitForAofAsync`
  execute queued writes and WAIT/WAITAOF on one fresh exclusive physical connection,
  closed after execution so replication history cannot leak between batches.
  Returned counts can be below the requested level; writes are not rolled back or
  replayed. Cluster execution requires one routing slot and surfaces redirects.
  These methods are not available inside transactions. See
  [durability acknowledgements](../website/docs/guides/durability-acknowledgements.md)
  for server versions, AOF configuration, partial failures, and timeout semantics.

### Cluster watched transactions

- After a successful WATCH, both standalone and Cluster clients invalidate the watched
  keys in the client-side cache. Reads started before WATCH cannot repopulate those entries,
  and subsequent reads fetch the current server value. Unrelated cached keys remain available.

- `CreateTransactionAsync(watchKeys)` supports Redis Cluster when all watched and queued
  keys share one effective hash slot. A dedicated node connection preserves WATCH through EXEC.
  MOVED, ASK, and READONLY rejections throw `RespireTransactionRetryException`; start a new
  WATCH attempt and re-read inputs before retrying. No watched transaction is automatically
  replayed on another connection. See the [Cluster WATCH guide](../website/docs/guides/batches-and-transactions.md#cluster-watch-transactions).

### Sorted sets

- Multi-key `PopManyAsync` adds ZMPOP and optional blocking BZMPOP; multi-key
  `PopAsync` adds BZPOPMIN/BZPOPMAX. Results include the selected, owned binary key
  with the view prefix removed, plus string or typed member/score entries.
  Batches and transactions support nonblocking `PopMany`. Existing single-key
  overloads keep their bindings. Implementers of `ISortedSetCommands` and
  `IBatchSortedSetCommands` must implement the new members.
  See the [sorted-set pop guide](SORTED_SET_POPS.md) for versions and wait semantics.

### Key sorting and database helpers

- `Keys.SortAsync` and `SortAsync<T>` support SORT/SORT_RO options, with `SortStoreAsync`
  for STORE's count result. `RandomAsync` returns an owned binary key from an unprefixed
  standalone database; `MoveAsync` moves a key between standalone databases. All have
  batch/transaction counterparts. Custom key-facet implementations must add these
  members. See [key sorting](KEY_SORTING.md) for prefix, Cluster, and version restrictions.


### Scripting

- `Functions` adds Redis 7+ function calls and library administration, with matching
  batch/transaction methods. `RespireFunctionLibrary` supports bounded immediate reloads.
  Existing client/queue implementers retain source compatibility through default facet
  properties; decorators should forward `Functions`. See the [scripting guide](SCRIPTING.md#redis-functions).

- `RespireScript.Create(source, readOnly: true)` selects Redis 7+ read-only Lua commands.
  Script cache management adds `ExistsAsync` and `FlushAsync`, with matching deferred
  operations and deferred `Load`. Existing external `IScriptCommands` and
  `IBatchScriptCommands` implementations remain source-compatible through default
  members that throw `NotSupportedException`; decorators must forward these new members
  to expose them. See the [scripting guide](SCRIPTING.md) for Cluster scope and versions.

### Breaking API changes

- `IStringCommands` adds both `SetManyIfNotExistsAsync` overloads, and
  `IBatchStringCommands` adds `SetManyIfNotExists` for atomic MSETNX. External
  implementations, decorators, and mocks must implement or forward these members.
  Existing `SetManyAsync`/`SetMany` overloads retain their bindings and unconditional
  behavior. See the [bulk string guide](../website/docs/commands/strings-and-keys.md#bulk-operations).

- `IRespireCommandQueue` adds `Streams`, exposing non-blocking stream operations on batches
  and transactions. External queue implementations, decorators, and mocks must implement
  or forward this property. This is an intentional pre-release compile-time break: new Streams
  support requires an explicit implementation, following the [API policy](API_DESIGN.md). The facet supports `Add`, `Count`, `Range`, `Remove`,
  `TrimByMaxLength`, and `Acknowledge`; blocking reads and consumer loops remain client-only.
  See the [batch and transaction guide](../website/docs/guides/batches-and-transactions.md).

- `IKeyCommands` and `IBatchKeyCommands` add absolute expiration and OBJECT metadata
  members. External implementations, decorators, and mocks must implement or forward
  `ExpiryTimeAsync`/`ExpiryTime` (both overloads), `EncodingAsync`/`Encoding`,
  `IdleTimeAsync`/`IdleTime`, `FrequencyAsync`/`Frequency`, and
  `ReferenceCountAsync`/`ReferenceCount`. These APIs are mirrored by batches and
  transactions. See the [key metadata guide](../website/docs/commands/strings-and-keys.md#absolute-expiration-and-object-metadata).

- Count-based set and sorted-set pops are now named `PopManyAsync(key, count, ...)`.
  Rename the corresponding batch and transaction calls from `Pop` to `PopMany`,
  including typed sorted-set calls. Scalar `PopAsync(key, ...)` and `Pop(key, ...)`
  keep their names and return one member; the `Many` methods return arrays.
  List APIs keep their existing `LeftPopManyAsync`/`RightPopManyAsync` names.
  No compatibility aliases are retained during this pre-release API cleanup.
  See [#365](https://github.com/thomhurst/Respire/issues/365) and the
  [collection API conventions](API_DESIGN.md#single-member-and-multi-member-pops).

- Multi-endpoint comma-delimited connection strings now require `cluster=true` for
  Cluster seeds or `serviceName=...` for Sentinel discovery. Standalone
  `RespireOptions.Endpoints` must not contain more than one endpoint; extra endpoints
  previously ignored now cause configuration validation to fail. For connection-time
  fallback between independent deployments, pass separate options to
  `RespireClient.ConnectAnyAsync`. See [#272](https://github.com/thomhurst/Respire/issues/272)
  and the [connection guide](../website/docs/fundamentals/connections.md).

- Pub/Sub message channels and subscription targets now use the owned, binary-safe
  `RespireChannel` value: `RespireMessage.Channel`, nullable `RespireMessage.Pattern`,
  and `RespireSubscription.Targets` no longer expose strings. Use `.Bytes` for exact
  identity and `.ToString()` for UTF-8 display text. Existing string subscription and
  publication overloads remain available; implicit byte-buffer conversions copy their
  inputs. Channel equality compares bytes independently of literal/pattern/sharded
  metadata. See [#300](https://github.com/thomhurst/Respire/issues/300) and the
  [Pub/Sub guide](../website/docs/guides/pub-sub.md).
