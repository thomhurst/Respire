---
title: Command retry policy
---

Set `RespireOptions.RetryPolicy` to retry buffered immediate commands on standalone,
Sentinel and Cluster clients. The default is `null`: the client uses its ordinary dispatch
path and never retries automatically.

```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new("localhost", 6379) },
    RetryPolicy = new RespireRetryPolicy
    {
        MaxAttempts = 3,
        MaxCommandRetryCategory = RespireCommandRetryCategory.ReadOnly,
    },
});
```

Typed commands, converted results and generated catalog commands all use the same retry
logic. Respire copies a command's arguments before it sends them, so changing your buffers
after the call cannot change a resend. One deadline and one cancellation token cover
connection selection, every send and every backoff delay. Client-side cache invalidation
stays correct across attempts, including for an attempt that is cancelled while its reply
is still on the wire.

Typed SET and ZADD refine their category only for audited option sets:

- `SET key value` and `SET key value XX` are `WriteLastWins`.
- `SET key value NX` is `WriteChecked`, the same as `SETNX`.
- `ZADD key XX` is `WriteLastWins`, and `ZADD key NX` is `WriteChecked`.

SET with an expiry, `KEEPTTL` or `GET`, ZADD with `INCR`, `CH`, `GT` or `LT`, and
XADD without idempotency keep their conservative audited categories. XADD with `IDMP`
or `IDMPAUTO` is `WriteChecked`; see [idempotent stream appends](#idempotent-stream-appends). A checked write checks its
condition again, which may prevent another write. If another caller removes the
key or member between attempts, `SET NX` or `ZADD NX` can write again. A replay
can report a different result, such as "not set" for a write that the first
attempt applied. An overwrite can replace an intervening update. Increasing
retry risk can repeat effects and cannot recover the original reply.

Generated catalog commands, such as `RespireCommands.String.SET`, do not inspect
their caller arguments. They use the descriptor's category for every argument
list. To allow a narrower category, declare it with `WithRetryCategory`.

## Idempotent stream appends

A plain `XADD` that loses its reply after the server appended the entry would append
a duplicate when resent, so it is `WriteAccumulating`. Redis 8.6 deduplicates appends
that carry a producer ID and message ID (`IDMP`): a resend returns the original entry ID.

When `RetryPolicy` selects `ConnectionLoss` and its `MaxCommandRetryCategory` is
`WriteChecked` or higher, typed `Streams.AddAsync` calls attach that identity
automatically. Each client has one producer ID, and each call gets a new random
16-byte message ID that every resend of that call reuses. Only these calls become
`WriteChecked`.

```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new("localhost", 6379) },
    RetryPolicy = new RespireRetryPolicy
    {
        MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked,
    },
    StreamProducerId = "orders-service", // optional; null generates "respire-<guid>" per client
});

// A lost reply is retried; the stream still contains exactly one entry.
RespireStreamId id = await client.Streams.AddAsync("orders", ("status", "paid"));
```

Calls that set `StreamAddOptions.Idempotency` or an explicit entry ID keep the caller's
choice. Explicit `IDMP` and `IDMPAUTO` are also `WriteChecked`. Batch and transaction
`Streams.Add` members do not receive an automatic identity: without explicit
idempotency they remain `WriteAccumulating` and are not retried after possible
execution at a `WriteChecked` maximum. Generated catalog
`RespireCommands.Stream.XADD` calls keep `WriteAccumulating` for every argument list.

A server that does not support `IDMP` rejects the option before it runs XADD. The
specific `ERR Invalid stream ID specified as stream command argument` rejection
resends that call as plain XADD, with its `WriteAccumulating` category. Other errors
reach the caller without a fallback. Each call attempts IDMP independently, so
Cluster shards, Sentinel failovers and rolling upgrades do not share stale
capability state. Older servers therefore receive one rejected IDMP attempt per
append before the ordinary XADD. Redis keeps deduplication history only within each stream's
`IDMP-DURATION` and `IDMP-MAXSIZE` limits (see `Streams.ConfigureAsync`). A retry that
arrives after its identity expires or is evicted appends again.

## Topologies

On standalone and Sentinel clients, Respire knows whether each failed attempt's frame
was [definitely unwritten](./transport-write-evidence) or may have reached Redis. Each
Sentinel attempt uses the current primary. A `READONLY` reply or a lost primary connection
makes the client rediscover the primary before the retry, so the retry writes to the
promoted primary.

Cluster attempts keep their usual `MOVED`, `ASK` and `READONLY` handling. A retry starts
only after an attempt's final failure. Cluster routing does not report whether a frame was
written, so every failed Cluster attempt counts as possibly executed: only the category
permission can admit another attempt. The deadline bounds every redirect inside every
attempt, and redirects never count as retry attempts.

## Caller-declared categories

Respire never infers retry permission from a caller-supplied command name or from
read routing. Declare the risk of the complete invocation explicitly:

```csharp
var read = RespireCommand.Create("MYMODULE.GET")
    .WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
using var result = await redis.ExecuteAsync(read, ["key"]);

var script = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true)
    .WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
var value = await redis.Scripts.ExecuteStringAsync(script, ["key"]);

var function = RespireFunction.Create("reader", readOnly: true)
    .WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
```

The declaration replaces any audited category on that descriptor, and Respire does
not validate it against the name or arguments. Scripts and functions default to
`Never`, including read-only ones. A declared script or function retries its complete
exchange, including the `EVALSHA`/`EVAL` fallback or a library reload. Its arguments
are copied before the first send. Every failed script or function attempt counts as
possibly executed, because it may have run before its reply was lost.

Raw `EVAL` and `FCALL` sent through `ExecuteAsync`, blocking commands, interpolated
commands, and fire-and-forget sends do not retry. Use `RespireScript` or
`RespireFunction` with a declared category instead.

## Failover groups

Set `RespireFailoverGroupOptions.RetryPolicy` and run operations through
`RespireFailoverGroup.ExecuteAsync`:

```csharp
RespireFailoverCandidate[] candidates =
[
    new(new RespireOptions { Endpoints = { new("primary.example", 6379) } }, Priority: 0),
    new(new RespireOptions { Endpoints = { new("standby.example", 6379) } }, Priority: 1),
];
await using var group = await RespireFailoverGroup.ConnectAsync(candidates, new RespireFailoverGroupOptions
{
    RetryPolicy = new RespireRetryPolicy { MaxAttempts = 3 },
});
var value = await group.ExecuteAsync(
    (client, token) => client.GetStringAsync("key", token),
    RespireCommandRetryCategory.ReadOnly);
```

Each attempt reads `ActiveClient` again, so a retry after a switch uses the newly
active member. When no member is healthy, the attempt fails as a connection failure
and can retry within the backoff budget. The group cannot inspect the operation, so
its category is required and every failed attempt counts as possibly executed.
`RespireCommandRetryCategory.Never` runs the operation once. Without a group
`RetryPolicy`, `ExecuteAsync` also runs the operation once.
Members keep their own `RetryPolicy`; a group retry starts only after a member's
operation fails. The group has no command timeout of its own. Each attempt uses its
member's timeout, and the caller's token bounds the whole operation.

For batches and transactions, create and execute the complete unit inside the delegate.
Each group retry then creates a fresh unit on the newly active member. Do not retain a
batch, transaction or member client between attempts. Declare a category that permits
every queued member's possible repeated effects. An open member circuit ends the
operation; the group never retries that circuit rejection against the same endpoint.

## Policy settings

| Setting | Default | Bounds and meaning |
| --- | --- | --- |
| `MaxAttempts` | `3` | From 1 through 100, including the initial send. 1 allows no retry. |
| `MaxCommandRetryCategory` | `ReadOnly` | Maximum accepted risk after possible execution. `Never` disables retries. |
| `RetryableErrors` | `ConnectionLoss`, `Loading`, `TryAgain`, `MasterDown`, `ReadOnly` | Only these named flags are supported. `None` selects no failures. |
| `InitialBackoff` | 10 milliseconds | First retry's delay ceiling. Zero disables delay. At most 1 minute. |
| `MaxBackoff` | 1 second | At least `InitialBackoff`, at most 1 minute. |
| `Jitter` | `1` | Finite fraction from 0 through 1. 0 uses the ceiling; 1 randomizes from zero to the ceiling. |

The delay ceiling doubles after each completed attempt, capped at `MaxBackoff`.
With partial jitter, the delay ranges from `ceiling * (1 - Jitter)` to the ceiling.
Each retry must fit within the original deadline; the policy never starts a new
timeout budget. Cancellation stops further retries. Respire checks the deadline and
cancellation again after each backoff, immediately before the next send.

## Execution risk

The categories form these permission sets, in increasing order of risk:
`Always`; then `Connection`; then `ReadOnly`; then `WriteChecked`; then
`WriteLastWins`; then `WriteAccumulating`; then `ServerAdmin`. Selecting a category
permits it and every category before it in that list. `Never` and undefined values
never authorize a retry.

A known command whose frame is **definitely unwritten** can be retried under any
enabled category selection. Both an **unknown** write outcome and **possible
execution** require the category permission above. An exception's message or
inner exception never changes that outcome. Unknown commands and scripts need an
explicit caller category; recognizing a command name does not grant permission.

Even a repeated read can return different data. A checked write can return a different
result after its first effect. An overwrite can destroy an intervening update.
Selecting `WriteAccumulating` or `ServerAdmin` explicitly accepts duplicate effects,
consumed data, and lost original results. Retrying never provides exactly-once
execution or recovers the original reply.

## Failures and dispatch scope

A retry needs both a selected failure and a permitted execution risk. Server replies
`LOADING`, `TRYAGAIN`, `MASTERDOWN`, and `READONLY` are individually selectable.
Authentication failures, exhausted connection recovery, protocol and configuration
errors, cancellation, and circuit breaker rejections are never retried. Other server
errors, including `BUSY` and `CLUSTERDOWN`, cannot be selected. Cluster routing handles
`MOVED` and `ASK` itself.

A `READONLY` retry refreshes the primary route first. Each attempt selects the current
connection (standalone) or the current primary (Sentinel). A circuit rejection is never
retried against the same endpoint, even though the command was not sent; a retry that
reaches another endpoint must be admitted there. See
[retries and circuit breakers](./circuit-breakers#retries) for details.

These operations do not retry: immediate replica reads, streamed commands, fire-and-forget sends, individual batch or transaction members (the
complete unit retries instead, as described below), connection protocol commands, cursor
continuations, and commands categorized as `Never`. Within a Cluster batch, a slot group
routed to replicas does retry as a group. A raw command never gains retry permission from
its name: use an audited `RespireCommands` descriptor or declare a category.

On a Cluster client with client-side caching, a read that populates the cache retries as
one operation, including its `CLIENT CACHING` prelude and redirects. Concurrent callers
waiting for the same entry receive one published result.

The `respire.command.retries` counter records `respire.command.retry.category` and
`respire.command.retry.outcome` (`attempted`, `succeeded` or `exhausted`). It is
recorded only when `RespireMetricGroups.Resiliency` is selected.

- On standalone and Sentinel clients, `attempted` counts resends the transport accepted.
  Cluster, script, function and failover-group retries have no such acceptance signal, so
  `attempted` counts started resends.
- `succeeded` and `exhausted` report one final outcome per operation that made a resend.
- A resend rejected by a circuit breaker before it sends anything records no `attempted`.
  A script's `EVAL` fallback, a function's library reload, or a failover-group operation can
  run other commands before a later one is rejected, so such a resend counts as `attempted`.
  Cluster script and function retries count every started resend, including one whose first
  command is rejected.
- Callers waiting on a shared cache read do not duplicate retry events. A throwing metrics
  exporter cannot change retry decisions or replies.

### Batches

`RetryPolicy` retries a batch as a complete unit. Standalone and Sentinel batches retry the
whole pipeline. Cluster batches retry each slot group independently: a retry resends every
member of that group, including members that succeeded, while other groups keep their
results. Cluster routing still handles `MOVED`, `ASK` and node retirement within an attempt,
and a retry waits for the slot owner's replacement before selecting a connection again. A
read-only group under a replica read policy retries on the replica route. A fresh cursor page
(cursor `0`) in that group records the server that answered its final attempt, so the next
page continues on the server that issued the cursor.

A batch is not atomic: Redis can execute some or all members before a failure. The rules:

- Replies stay pending until the retry decision is final, and only the final attempt's
  results are published.
- A selected failure permits a resend only when the policy permits every member's category.
  Once any send has started, the whole unit counts as possibly executed. A connection
  acquisition failure before any send counts as definitely unwritten.
- For example, a successful `INCR` alongside a failed `GET` prevents a replay under the
  default read-only policy. With `WriteAccumulating` permitted, a transient failure from the
  `GET` replays the already-successful `INCR` and duplicates its effect.
- Cancellation or a timeout during backoff faults every pending, including members that
  already succeeded; the abandoned attempt's replies are not published.
- Every retry resends every member in queue order with the original argument bytes, and
  each command is admitted by the circuit breaker again. One deadline and the caller's
  cancellation token cover acquisition, admission, replies and backoff.

Retry telemetry counts accepted resends and one final outcome per batch that made a resend,
tagged with the riskiest member category. A retry decision followed only by a failed
acquisition or circuit admission records no retry event. Cluster batches count started
resends after route selection, excluding a group rejected entirely by open circuits, and
one final outcome per retried group. Hash import sessions and server cursor continuations
do not retry. Watched transaction helpers keep their own complete-callback retries.

### Transactions

On standalone, Sentinel and Cluster clients, the same `RetryPolicy` retries an unwatched
`RespireTransaction` as one complete unit. The client resends the retained `MULTI`, queued
commands and `EXEC` frame together on the current primary. It never resends a subset of the
members.

Cluster transactions keep MOVED and node-retirement recovery within an attempt. Routing
rejections do not consume the retry budget. A retry waits for the slot owner's replacement
and selects the current owner. Transactions never follow ASK; an ASK rejection ends the
unit, including during a retry.

The transport records one write outcome for the whole frame. A resend needs both a selected
failure and permission for every member's category under that outcome. One member is
enough to block the unit:

- A unit that holds `INCR` stays at one send after a lost `EXEC` reply, unless
  `MaxCommandRetryCategory` accepts `WriteAccumulating`. This applies even when the other
  members are reads.
- A raw member, script or function without a declared category is `Never` and blocks the
  unit. Deferred raw members, scripts and functions carry the caller's declared category,
  as their immediate dispatch does. Marking a script or function read-only does not by
  itself declare its retry category.

A lost `EXEC` reply counts as a possible execution, not as an abort. A selected queue error,
such as `LOADING`, makes Redis discard the transaction with `EXECABORT`. The frame was still
written, so the same category rule applies. Selected member errors inside an `EXEC` result
array also retry the complete unit before any member result is published. Other members may
already have succeeded, so permission for every member remains necessary.

The retry keeps one deadline and the caller's token across attempts and backoff.
Member pendings complete once, from the final attempt. Telemetry reports `attempted` for each
accepted resend, even if its frame fails before it is written. It reports `succeeded` or
`exhausted` once per unit that made a resend. A replacement wait or circuit rejection before
any accepted resend records neither outcome. The category tag names the riskiest member category.

The client never resends these units:

- Watched transactions. Their `WATCH` state belongs to the lost connection.
  `RunTransactionAsync` and `RunTransactionWithReadsAsync` rebuild the whole callback for
  permitted transient failures, as described in
  [watched transaction retries](./batches-and-transactions#automatic-watched-transaction-retries).
- Hash import sessions.
- Units that hold a server-local cursor continuation.

## Performance and test coverage

With `RetryPolicy = null`, commands take the ordinary dispatch path. An enabled policy adds
work to every retry-eligible command, even when nothing fails.

Fake-server tests cover the category and failure matrix, deadlines, cancellation, client-side
caching and metrics. Real Redis tests discard replies after the server executed a command, then
check the stored effects, including after Sentinel promotion, failover-group switching and Cluster
slot migration. With the default read-only maximum, a lost increment reply leaves one effect;
an explicit `WriteAccumulating` maximum permits two. Fake topology tests alone do not prove real
failover behavior, and no test covers every API, topology and fault combination.

The `run-command-retry-benchmarks` pull request label runs `CommandRetryBenchmarks`, which
compares a null policy, a disabled policy (`MaxAttempts = 1`) and the default enabled policy
on healthy reads. These measurements describe healthy round trips only, not retry behavior.

### Completed policy comparison

[Run 38243810841](https://github.com/thomhurst/Respire/actions/runs/38243810841)
(12 October 2026, net10.0, Redis 8.10, BenchmarkDotNet `MediumRun`) measured a `STRLEN` read
through four paths. Means are within the candidate revision:

| Path | Enabled vs null policy latency | Allocated per operation: null / disabled / enabled |
| --- | ---: | ---: |
| Typed command | +4.59% | 0 / 0 / 1308 B |
| Generated catalog command | +5.26% | 774 / 778 / 2067 B |
| Declared read-only script | +3.09% | 1393 / 1401 / 2554 B |
| Failover-group delegate | no consistent separation | 0 / 0 / 0 B |

The typed, catalog and script latency differences have non-overlapping confidence intervals
and appear in both control revisions. Null and disabled policies did not separate for any path.
The catalog and script allocations under a null policy are not caused by retries. Whole-process
memory snapshots before connection, at idle, after churn and after disposal were similar across
revisions; they include runtime and pool retention and are not per-command allocations.

### Completed owner and historical-default comparisons

[Owner-policy run 38250362487](https://github.com/thomhurst/Respire/actions/runs/38250362487)
(`ShortRun`) compared the policies for each topology. Null and disabled integer reads allocated
0 B per operation. An enabled policy allocated 1321 B standalone, 1667 B Sentinel and 1743 B
Cluster; a declared function allocated 784 / 771 / 1871 B (null / disabled / enabled). The
`ShortRun` latency intervals were too wide to establish equivalence or zero overhead. That run
predates pooled retry frames, which later reduced per-snapshot allocations.

[Historical-default run 38250366580](https://github.com/thomhurst/Respire/actions/runs/38250366580)
compared null-policy reads with a revision from before retries existed. Integer reads still
allocated 0 B. The Cluster mean rose 2–4%, but that comparison includes unrelated intervening
changes and does not attribute the difference to retries.

Enabled policies allocate on every eligible command. Do not assume zero-allocation dispatch
for raw, catalog, script, function or cache paths when a policy is enabled.

### Whole-unit acceptance

Batch and transaction retries are covered by fake-server tests for permission checks
(every member must permit a replay), deferred publication of results, conservative handling
of partial writes and lost replies, single settlement under cancellation and deadlines, and
independent WATCH-conflict and transient budgets. Real Redis tests check whole-unit effects
after lost replies, including across Sentinel promotion and Cluster slot moves. Lifetime tests
cover client disposal during backoff, conversion failures and independent commands interleaved
with a pending unit retry. Retries of fresh Cluster replica cursor pages keep their cursor affinity.

`UnitRetryBenchmarks` measures a healthy two-member read-only batch and unwatched transaction
on standalone, Sentinel and single-primary Cluster clients with each policy mode. A batch or
transaction allocates its queue and pendings in every mode, so the expected result is that
disabled and null policies allocate the same bytes. No completed comparison is reported here.
