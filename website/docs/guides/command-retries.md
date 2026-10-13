---
title: Command retry policy
---

Set `RespireOptions.RetryPolicy` to retry buffered immediate commands on standalone,
Sentinel and Cluster clients. The default is `null`: no automatic retries.

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

- Typed commands, converted results and generated catalog commands share the same retry logic.
- Respire copies arguments before sending, so changing your buffers after the call cannot change a resend.
- One deadline and one cancellation token cover connection selection, every send and every backoff delay.
- Client-side cache invalidation stays correct across attempts, including a cancelled attempt whose reply is still in flight.

## Policy settings

| Setting | Default | Bounds and meaning |
| --- | --- | --- |
| `MaxAttempts` | `3` | 1 through 100, including the initial send. 1 allows no retry. |
| `MaxCommandRetryCategory` | `ReadOnly` | Maximum accepted risk after possible execution. `Never` disables retries. |
| `RetryableErrors` | `ConnectionLoss`, `Loading`, `TryAgain`, `MasterDown`, `ReadOnly` | Only these named flags are supported. `None` selects no failures. |
| `InitialBackoff` | 10 milliseconds | First retry's delay ceiling. Zero disables delay. At most 1 minute. |
| `MaxBackoff` | 1 second | At least `InitialBackoff`, at most 1 minute. |
| `Jitter` | `1` | Finite fraction from 0 through 1. 0 uses the ceiling; 1 randomizes from zero to the ceiling. |

- The delay ceiling doubles after each attempt, capped at `MaxBackoff`.
- With partial jitter, the delay ranges from `ceiling * (1 - Jitter)` to the ceiling.
- Every retry must fit within the original deadline; the policy never starts a new timeout budget.
- Cancellation stops further retries. Respire checks the deadline and cancellation again right before each resend.

## Execution risk

Categories, in increasing risk: `Always`, `Connection`, `ReadOnly`, `WriteChecked`,
`WriteLastWins`, `WriteAccumulating`, `ServerAdmin`. Selecting a category permits it
and every category before it. `Never` and undefined values never authorize a retry.

- A known command whose frame is **definitely unwritten** can retry under any enabled category.
- An **unknown** write outcome or **possible execution** requires the category permission.
- An exception's message or inner exception never changes that outcome.
- Unknown commands and scripts need an explicit caller category; recognizing a command name grants nothing.

:::warning
A repeated read can return different data. A checked write can return a different result
after its first effect. An overwrite can destroy an intervening update. Selecting
`WriteAccumulating` or `ServerAdmin` accepts duplicate effects, consumed data and lost
original results. Retries never provide exactly-once execution or recover the original reply.
:::

### SET, ZADD and catalog commands

Typed SET and ZADD refine their category only for audited option sets:

| Command | Category |
| --- | --- |
| `SET key value`, `SET key value XX` | `WriteLastWins` |
| `SET key value NX` (same as `SETNX`) | `WriteChecked` |
| `ZADD key XX` | `WriteLastWins` |
| `ZADD key NX` | `WriteChecked` |
| XADD with `IDMP` or `IDMPAUTO` | `WriteChecked` (see [idempotent stream appends](#idempotent-stream-appends)) |

SET with an expiry, `KEEPTTL` or `GET`, ZADD with `INCR`, `CH`, `GT` or `LT`, and XADD
without idempotency keep their conservative categories.

A checked write re-checks its condition. If another caller removes the key or member
between attempts, `SET NX` or `ZADD NX` can write again. A replay can report "not set"
for a write the first attempt applied.

Generated catalog commands, such as `RespireCommands.String.SET`, do not inspect their
arguments: they use the descriptor's category for every argument list. Declare a narrower
category with `WithRetryCategory`.

## Failures and dispatch scope

A retry needs both a selected failure and a permitted execution risk.

- Selectable server replies: `LOADING`, `TRYAGAIN`, `MASTERDOWN`, `READONLY`.
- Never retried: authentication failures, exhausted connection recovery, protocol and configuration errors, cancellation, circuit breaker rejections.
- Not selectable: other server errors, including `BUSY` and `CLUSTERDOWN`. Cluster routing handles `MOVED` and `ASK` itself.

A `READONLY` retry refreshes the primary route first. Each attempt selects the current
connection (standalone) or current primary (Sentinel). A circuit rejection is never retried
against the same endpoint; a retry that reaches another endpoint must be admitted there. See
[retries and circuit breakers](./circuit-breakers#retries).

These do not retry:

- Immediate replica reads (a Cluster batch slot group routed to replicas does retry as a group).
- Streamed commands, blocking commands, interpolated commands and fire-and-forget sends.
- Individual batch or transaction members (the complete unit retries instead; see [batches](#batches)).
- Connection protocol commands and cursor continuations.
- Commands categorized as `Never`.
- Raw `EVAL` and `FCALL` through `ExecuteAsync`. Use `RespireScript` or `RespireFunction` with a declared category.

A raw command never gains retry permission from its name: use an audited `RespireCommands`
descriptor or declare a category.

On a Cluster client with client-side caching, a read that populates the cache retries as one
operation, including its `CLIENT CACHING` prelude and redirects. Concurrent callers waiting
for the same entry receive one result.

### Metrics

The `respire.command.retries` counter records `respire.command.retry.category` and
`respire.command.retry.outcome` (`attempted`, `succeeded` or `exhausted`). It is recorded
only when `RespireMetricGroups.Resiliency` is selected.

- Standalone and Sentinel: `attempted` counts resends the transport accepted. Cluster, script, function and failover-group retries count started resends.
- `succeeded` and `exhausted` report one final outcome per operation that resent.
- A resend rejected by a circuit breaker before sending anything records no `attempted`. A script's `EVAL` fallback, a function's library reload or a failover-group operation may run other commands first, so such a resend counts as `attempted`. Cluster script and function retries count every started resend.
- Callers sharing a cache read do not duplicate retry events. A throwing metrics exporter cannot change retry decisions or replies.

## Topologies

- **Standalone and Sentinel**: Respire knows whether each failed frame was [definitely unwritten](./transport-write-evidence) or may have reached Redis. Each Sentinel attempt uses the current primary; a `READONLY` reply or lost primary connection triggers rediscovery, so the retry writes to the promoted primary.
- **Cluster**: `MOVED`, `ASK` and `READONLY` handling stays the same, and a retry starts only after an attempt's final failure. Cluster routing does not report write evidence, so every failed attempt counts as possibly executed. The deadline bounds every redirect, and redirects do not count as attempts.

## Caller-declared categories

Respire never infers retry permission from a caller-supplied command name or from read
routing. Declare the risk of the complete invocation:

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

- The declaration replaces any audited category. Respire does not validate it against the name or arguments.
- Scripts and functions default to `Never`, even read-only ones.
- A declared script or function retries its complete exchange, including the `EVALSHA`/`EVAL` fallback or a library reload. Every failed attempt counts as possibly executed.

## Idempotent stream appends

A plain `XADD` that loses its reply after the append would duplicate the entry when resent,
so it is `WriteAccumulating`. Redis 8.6 deduplicates appends that carry a producer ID and
message ID (`IDMP`): a resend returns the original entry ID.

When `RetryPolicy` selects `ConnectionLoss` and `MaxCommandRetryCategory` is `WriteChecked`
or higher, typed `Streams.AddAsync` calls attach that identity automatically and become
`WriteChecked`. Each client has one producer ID; each call gets a random 16-byte message ID
that its resends reuse.

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

- Calls that set `StreamAddOptions.Idempotency` or an explicit entry ID keep the caller's choice. Explicit `IDMP` and `IDMPAUTO` are also `WriteChecked`.
- Batch and transaction `Streams.Add` members get no automatic identity. Without explicit idempotency they stay `WriteAccumulating` and do not retry after possible execution at a `WriteChecked` maximum.
- Generated `RespireCommands.Stream.XADD` stays `WriteAccumulating` for every argument list.

Older servers reject `IDMP` before running XADD. On the specific `ERR Invalid stream ID
specified as stream command argument` rejection, Respire resends that call as plain XADD
(`WriteAccumulating`). Other errors reach the caller. Each call tries IDMP independently, so
older servers see one rejected attempt per append.

:::warning
Redis keeps deduplication history only within each stream's `IDMP-DURATION` and
`IDMP-MAXSIZE` limits (see `Streams.ConfigureAsync`). A retry that arrives after its identity
expires or is evicted appends again.
:::

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

- Each attempt reads `ActiveClient` again, so a retry after a switch uses the new active member.
- With no healthy member, the attempt fails as a connection failure and can retry within the backoff budget.
- The category is required, and every failed attempt counts as possibly executed.
- `RespireCommandRetryCategory.Never`, or no group `RetryPolicy`, runs the operation once.
- Members keep their own `RetryPolicy`; a group retry starts only after a member's operation fails.
- The group has no command timeout. Each attempt uses its member's timeout; the caller's token bounds the whole operation.
- An open member circuit ends the operation; the group never retries it against the same endpoint.

For batches and transactions, create and execute the complete unit inside the delegate, so
each retry builds a fresh unit on the active member. Do not keep a batch, transaction or member
client between attempts. Declare a category that permits every queued member's repeated effects.

## Batches

`RetryPolicy` retries a batch as a complete unit.

- **Standalone and Sentinel**: the whole pipeline retries.
- **Cluster**: each slot group retries independently, resending every member of that group (including ones that succeeded); other groups keep their results. `MOVED`, `ASK` and node retirement are handled within an attempt. A read-only group under a replica read policy retries on the replica route, and a fresh cursor page (cursor `0`) keeps the server that answered, so the next page continues there.

A batch is not atomic: Redis can execute some or all members before a failure.

- Replies stay pending until the retry decision is final; only the final attempt's results are published.
- A resend needs a selected failure and permission for every member's category. Once any send starts, the whole unit counts as possibly executed. A connection acquisition failure before any send counts as definitely unwritten.
- Cancellation or a timeout during backoff faults every pending, including members that already succeeded.
- Every retry resends all members in queue order with the original bytes; each is admitted by the circuit breaker again. One deadline and token cover acquisition, admission, replies and backoff.

:::warning
A successful `INCR` beside a failed `GET` blocks a replay under the default read-only policy.
With `WriteAccumulating` permitted, a transient `GET` failure replays the `INCR` and duplicates
its effect.
:::

Telemetry counts accepted resends and one final outcome per retried batch, tagged with the
riskiest member category. A retry followed only by failed acquisition or circuit admission
records nothing. Cluster batches count started resends after route selection (excluding a group
rejected entirely by open circuits) and one outcome per retried group.

Hash import sessions and server cursor continuations do not retry. Watched transaction helpers
keep their own callback retries.

### Transactions

On all topologies, the same `RetryPolicy` retries an unwatched `RespireTransaction` as one
unit: the client resends `MULTI`, the queued commands and `EXEC` together on the current
primary, never a subset.

- Cluster transactions handle `MOVED` and node retirement within an attempt; routing rejections do not use retry budget. Transactions never follow `ASK`: an ASK rejection ends the unit.
- A resend needs a selected failure and permission for every member's category. One member is enough to block the unit: one `INCR` keeps the unit at one send after a lost `EXEC` reply unless `WriteAccumulating` is accepted, even if the other members are reads.
- A raw member, script or function without a declared category is `Never` and blocks the unit. Marking a script or function read-only does not declare its retry category.
- A lost `EXEC` reply counts as possible execution. A selected queue error such as `LOADING` makes Redis abort with `EXECABORT`; the frame was written, so the same rule applies. Selected member errors inside the `EXEC` array also retry the whole unit before any result is published.
- One deadline and token cover all attempts. Member pendings complete once, from the final attempt.
- Telemetry reports `attempted` per accepted resend and `succeeded` or `exhausted` once per retried unit, tagged with the riskiest member category.

The client never resends:

- Watched transactions: their `WATCH` state belongs to the lost connection. `RunTransactionAsync` and `RunTransactionWithReadsAsync` rerun the whole callback instead; see [watched transaction retries](./batches-and-transactions#automatic-watched-transaction-retries).
- Hash import sessions.
- Units holding a server-local cursor continuation.

## Performance

With `RetryPolicy = null`, commands take the ordinary dispatch path. An enabled policy adds
work and allocates on every retry-eligible command, even when nothing fails. Do not assume
zero-allocation dispatch for raw, catalog, script, function or cache paths with a policy enabled.

The `run-command-retry-benchmarks` pull request label runs `CommandRetryBenchmarks` on healthy
reads. [Run 38243810841](https://github.com/thomhurst/Respire/actions/runs/38243810841)
(12 October 2026, net10.0, Redis 8.10, BenchmarkDotNet `MediumRun`), `STRLEN` read:

| Path | Enabled vs null policy latency | Allocated per operation: null / disabled / enabled |
| --- | ---: | ---: |
| Typed command | +4.59% | 0 / 0 / 1308 B |
| Generated catalog command | +5.26% | 774 / 778 / 2067 B |
| Declared read-only script | +3.09% | 1393 / 1401 / 2554 B |
| Failover-group delegate | no consistent separation | 0 / 0 / 0 B |

Null and disabled (`MaxAttempts = 1`) policies did not separate. The catalog and script
allocations under a null policy are not caused by retries.
