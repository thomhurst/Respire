---
title: Command retry policy
---

Set `RespireOptions.RetryPolicy` to retry buffered immediate commands on standalone,
Sentinel and Cluster clients. The default is `null`: no automatic retries.

When composing with Polly v8 or Microsoft.Extensions.Resilience, [choose one retry
owner](./external-resilience) to avoid multiplying attempts.

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

Remaining performance acceptance is tracked in [#1693](https://github.com/thomhurst/Respire/issues/1693).
The separate [lifetime validation follow-up](https://github.com/thomhurst/Respire/issues/1694)
covers gaps beyond existing cancellation and pooled-reuse tests; allocation and retention
measurements alone do not prove race safety.

With `RetryPolicy = null`, commands take the ordinary dispatch path. An enabled policy adds
work and can allocate on retry-eligible commands, even when nothing fails. Do not assume
zero-allocation dispatch for raw, catalog, script, function or cache paths with a policy enabled.

### Healthy-path acceptance audit: 14 October 2026

**The audit is complete; the original zero-allocation/no-measurable-cost goal is not met.**
Issues [#862](https://github.com/thomhurst/Respire/issues/862),
[#1214](https://github.com/thomhurst/Respire/issues/1214) and
[#1499](https://github.com/thomhurst/Respire/issues/1499) are closed. Their closure is delivery
history, not performance acceptance. This [#1693](https://github.com/thomhurst/Respire/issues/1693)
audit assesses the completed evidence against main `720e76eba9ea19486d2417d4c5812516af44f9ab`.
It neither certifies that revision's performance nor repeats an unchanged benchmark for a
favorable result.

The existing `CommandRetryBenchmarks` covers typed/catalog/script/group reads.
`CommandRetryOwnerBenchmarks` covers standalone, Sentinel, Cluster, declared functions and
standalone/Cluster tracked cache producers. Cache cases **clear the local entry on every
invocation**: their cost includes clearing, a network producer, publication and conversion,
not a cache hit. `Disabled` means `MaxAttempts = 1`; it is not the error-metrics mode in the
[transport evidence](./transport-write-evidence#healthy-path-disposition-14-october-2026).

#### Targets and decision rule

The allocation target is **zero additional steady-state bytes caused by retry support**,
with warmed pools and identical inputs. This preserves the original goal without requiring
existing result conversion or cache publication to become allocation-free. It is an
optimization target, not a public zero-allocation guarantee or permission to weaken safety.

| Healthy path | Allocation target | Latency comparison and reason |
| --- | --- | --- |
| Default/Disabled typed integer, standalone/Sentinel/Cluster | 0 B/op | No added per-command cost; both modes select ordinary dispatch. Compare the same mode across revisions, and Disabled against Default. |
| Default/Disabled catalog, script, function and forced cache producer | 0 incremental B/op over the same owner/result shape | Preserve existing conversion/publication costs; compare identical fixture work rather than an integer-only path. |
| Enabled typed/catalog, standalone/Sentinel/Cluster and both cache producers | 0 incremental retry B/op over that owner's Default path | Healthy first attempts should reuse warmed ownership where safe; snapshotting and immutable write evidence must survive cancellation and source reuse. This target remains unmet. |
| Enabled script/function/group logical owner | 0 incremental retry B/op over the same invocation with Default | Preserve fallback/reload/delegate semantics and one deadline. An overlapping interval does not prove a free logical retry wrapper. |
| Transport PING/error/SET/pipeline, batches, streams and circuit GET | 0 incremental B/op over the same operation, payload, connection count and metric mode | Preserve existing payload, batch and fixture allocations; compare per-command pipeline rows and per-batch rows in their own units. |

For future changes, use a **1% latency non-inferiority budget per row**, with at most 1%
absolute B/A control drift. One percent limits the added cost to about 0.7 microseconds for
the roughly 70-microsecond owner reads in the aggregate run; it prevents a broad network
interval from hiding multi-percent overhead. It is an explicit engineering budget for the
next experiment, **not a retrospective relaxation or satisfaction of the original strict
goal**. Require the candidate's upper reported interval bound divided by each control's
lower bound to be at most 1.01, with positive bounds, and inspect every launch. For a policy
comparison, also apply that bound to Enabled/Default (or Disabled/Default) within each phase.
This conservative interval envelope is a screening rule, not a confidence interval for the
ratio. Wider intervals, larger drift, missing attribution or inconsistent launches mean
**unestablished**, not equivalence. A separated slowdown or positive attributed allocation
means the strict goal is unmet. Never drop an unfavorable launch or widen the budget after
seeing results. No historical run below is promoted to acceptance using this new rule.

#### Completed owner evidence and remaining gaps

Both downloaded artifacts were available and unexpired on 14 October. They preserve the
revision manifest, native logs, JSON reports, validation results and four-stage full-GC
snapshots. All phases use the candidate's fixture. Both run on Linux/.NET 10.0.12, SDK
10.0.401, BenchmarkDotNet 0.15.8 and Redis 8.10; they do not establish Windows, TLS or net8.0
performance. The measured merge SHA is distinct from the event head.

| Run / artifact | A and B library | Candidate merge / event head | Sampling |
| --- | --- | --- | --- |
| [38255514678](https://github.com/thomhurst/Respire/actions/runs/38255514678), `command-retry-owner-policies-net10.0` (11749779341) | `a19b9590235f6d2ad0f0e7d41dfafccbbb9b5394` | `1f5c332e4ac317cd5ae2942e6a1465856a15c999` / `7b5b4b5ec2b02b5b303efa718373256885c80768` | 15 cases; ShortRun, one launch, three actual iterations; EPYC 7763 |
| [38264467525](https://github.com/thomhurst/Respire/actions/runs/38264467525), `command-retry-aggregate-audit-net10.0` (11755872410) | `a19b9590235f6d2ad0f0e7d41dfafccbbb9b5394` | `01b15844a90871d24d5536531a3c69df308a345c` / `fa7360a2d458d8253e5eab9bc789b9c06a7ba595` | 18 cases; MediumRun, two launches, 15 actual iterations each; EPYC 9V45 |

The pooling run reduces reported Enabled allocations to 1066/1085/1450 B/op for
standalone/Sentinel/Cluster and 3711 B/op for the cache producer; Function remains 1865 B/op.
Every candidate latency interval overlaps both controls, with B/A drift -5.76% to +1.84%.
Its single ShortRun launch cannot establish latency equivalence. These numbers must not be
subtracted from the later run's totals: the runners and experiments differ.

The aggregate run supplies the previously missing tracked Cluster producer result:

| Owner | Candidate B/op: Default / Disabled / Enabled | Disposition |
| --- | ---: | --- |
| Standalone | 0 / 0 / 1107 | Default/Disabled allocation target observed; Enabled zero-increment target unmet. Enabled is faster than both historical controls, not proven equivalent to Default. |
| Sentinel | 0 / 0 / 1111 | Default/Disabled allocation target observed; Enabled target unmet. Ready-route reuse removes a known discovery owner, not all allocation. |
| Cluster | 0 / 0 / 1508 | Enabled allocation target unmet. Default is +0.66%/+4.81% against A/B, separated above B; drift -3.96%. Latency acceptance unestablished. |
| Function | 792 / 778 / 1924 | Existing result costs persist; Enabled adds work. No zero-increment acceptance. |
| CacheProducer | 2669 / 2671 / 3777 | Enabled target unmet. Default is +4.47%/+9.13%, separated above B; drift -4.26%. Clear/publication costs prevent attributing the total to retries. |
| ClusterCacheProducer | 3293 / 3267 / 4577 | Enabled target unmet. Default is +4.14%/+7.99%, separated above B; drift -3.56%. The old control did not implement tracked retries, so its Enabled allocation is not equivalent functionality. |

Across all 18 aggregate rows, B/A drift ranges from -7.86% to +4.14%.
Within-phase launch means also differ: the largest candidate difference is 4.37% for
CacheProducer/Disabled (74.760 versus 71.494 microseconds); control B Cluster/Disabled
launches differ by 7.53% (66.113 versus 71.089 microseconds). The complete tables
below preserve every interval and allocation, including Disabled and favorable rows.
The older pre-retry historical Default Cluster signal also remains unattributed:
[run 38250366580](https://github.com/thomhurst/Respire/actions/runs/38250366580) measured
69.193 microseconds [68.204, 70.183], versus A 66.778 [65.483, 68.072] and B 67.737
[67.036, 68.438], +3.62%/+2.15%, drift +1.44%, all 0 B/op. Its intervening changes and
separation only from A prevent isolated retry attribution. New favorable means do not
erase that historical signal.

#### Cause isolation against current main

`ClientCore` still normalizes a disabled policy to `CommandRetries = null`, so Default and
Disabled share the dispatch branch. `ImmediateRetryState` still pools at most 256 owners,
captures bytes and slot synchronously, clears the entire rental and releases its buffer on
return. The owner source is unchanged from the aggregate candidate. The
[pooling investigation](https://github.com/thomhurst/Respire/pull/1517) isolated 120 B per
snapshot becoming zero after warmup and a healthy Sentinel discovery closure; neither is
the remaining end-to-end allocation total. Reimplementing those fixes would duplicate work.

Current `SendAttemptAsync` still creates a non-pooled `CommandWriteObservation` per attempt
(48-byte objects in the earlier allocation trace). Deadline cancellation, reply conversion,
logical wrappers and cache publication have distinct owners. That identifies a real remaining
allocation site, **not the cause of every reported byte or latency difference**. The logical
retry/circuit composition and transaction support changed after the measured revision;
main also contains later endpoint-context changes. The current owner fixture is unchanged
from the aggregate candidate, but its results cannot be relabeled as measurements of main.

Before a production optimization, isolate the remaining owner with matching allocation
stacks and held-reply/cancellation controls, distinguish pooled suspension misses from
steady-state costs, and establish whether default-path latency follows source changes or
runner regimes. Preserve retry categories, original deadlines, FIFO retirement, immutable
write outcomes and borrowed-buffer ownership. Transport's completed-fill cause and
unresolved selection/stream/batch regimes have their own
[disposition](./transport-write-evidence#healthy-path-disposition-14-october-2026).
This audit makes no hot-path change and requests no new comparison. A material future change
uses only its applicable existing label: `run-command-retry-benchmarks` for explicit requests,
`run-command-retry-owner-benchmarks` for topology/producers, or `run-transport-benchmarks`
for transport. Keep the net10.0 pinned A/candidate/B comparison and assess results on a later
pass. Whole-unit changes use the existing `UnitRetryBenchmarks` and `run-unit-retry-benchmarks`.

#### Retention limits

Aggregate candidate process medians, before-connect / connected-idle / after-churn /
after-dispose, are managed **137960 / 2377164 / 2421024 / 2073772 B** and working set
**46147584 / 70318080 / 72026112 / 93607936 B**. Corresponding post-dispose managed medians
are 2074640 B for A and 2081116 B for B. Candidate POH rises from 8184 to 24528 B and remains
there after disposal. Pooling-run candidate managed medians are
137464 / 2010856 / 2042232 / 1748088 B. These descriptive medians combine different owners;
the parameter-specific candidate rows below and the artifact's full A/B rows retain that
distinction. Each snapshot follows a forced compacting GC. Runtime, fixtures, clients and
pools contribute; these snapshots prove neither a leak nor complete reclamation.

The 256-owner pool capacity limits retained idle owners, not outstanding operations. It
does not establish a global byte cap. Transport observations can retain a connection graph
until their owners finish; canceled frames can remain in flight. Keep the
[write-ownership limits](./transport-write-evidence#ownership-and-retention-limits).

#### Complete owner comparison records

Intervals and means below are nanoseconds; allocations are B/op. Negative ShortRun
interval bounds are preserved, not clipped into evidence of equivalence. Aggregate
intervals combine two launches; reported allocation is the exporter value, not a
stack-attributed total. The JSON reports confirm 135 pooling and 1620 aggregate actual
workload samples across A/candidate/B. Recomputed means, interval bounds, deltas and
allocation columns match the published comparison tables. Retention rows are medians
of one (pooling) or two (aggregate) fixture processes per owner/policy/stage; complete
control rows, POH and working-set columns remain in each linked run artifact.

<details>
<summary>Run 38255514678: every owner/policy interval, allocation and control drift</summary>

| Case |A ns [interval] | Candidate ns [interval] | B ns [interval] | Candidate/A | Candidate/B | B/A drift | Allocated B (A/candidate/B) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Read Owner=CacheProducer&Policy=Default | 207892.581 [-16969.512, 432754.675] | 204432.246 [190498.957, 218365.535] | 195909.025 [43188.997, 348629.053] | -1.66% | +4.35% | -5.76% | 2624/2630/2640 |
| Read Owner=CacheProducer&Policy=Disabled | 203300.646 [191805.819, 214795.473] | 203472.074 [155653.645, 251290.502] | 201881.708 [187196.625, 216566.791] | +0.08% | +0.79% | -0.70% | 2638/2638/2637 |
| Read Owner=CacheProducer&Policy=Enabled | 204014.461 [103261.848, 304767.073] | 203479.727 [140555.628, 266403.826] | 207763.749 [147494.428, 268033.070] | -0.26% | -2.06% | +1.84% | 3860/3711/3882 |
| Read Owner=Cluster&Policy=Default | 190521.786 [182822.045, 198221.526] | 191278.372 [174449.713, 208107.032] | 190276.722 [181246.352, 199307.093] | +0.40% | +0.53% | -0.13% | 0/0/0 |
| Read Owner=Cluster&Policy=Disabled | 191567.334 [185551.783, 197582.886] | 191355.434 [184314.074, 198396.794] | 190321.903 [159564.088, 221079.718] | -0.11% | +0.54% | -0.65% | 0/0/0 |
| Read Owner=Cluster&Policy=Enabled | 194869.897 [171163.141, 218576.653] | 196091.328 [173460.087, 218722.568] | 196323.815 [179537.494, 213110.136] | +0.63% | -0.12% | +0.75% | 1699/1450/1697 |
| Read Owner=Function&Policy=Default | 200162.861 [194647.938, 205677.784] | 201776.721 [178321.444, 225231.997] | 198987.084 [178446.299, 219527.868] | +0.81% | +1.40% | -0.59% | 769/770/768 |
| Read Owner=Function&Policy=Disabled | 199572.805 [191047.746, 208097.864] | 201871.761 [184975.114, 218768.409] | 201648.515 [186049.123, 217247.906] | +1.15% | +0.11% | +1.04% | 767/770/773 |
| Read Owner=Function&Policy=Enabled | 204862.363 [178061.425, 231663.301] | 203502.899 [185364.641, 221641.157] | 203458.314 [157300.529, 249616.099] | -0.66% | +0.02% | -0.69% | 1848/1865/1850 |
| Read Owner=Sentinel&Policy=Default | 189894.974 [184183.848, 195606.099] | 190828.295 [188676.291, 192980.299] | 190831.953 [179676.365, 201987.541] | +0.49% | -0.00% | +0.49% | 0/0/0 |
| Read Owner=Sentinel&Policy=Disabled | 190168.083 [178420.867, 201915.298] | 190229.427 [183100.621, 197358.233] | 189985.182 [181299.848, 198670.516] | +0.03% | +0.13% | -0.10% | 0/0/0 |
| Read Owner=Sentinel&Policy=Enabled | 196415.600 [179255.963, 213575.238] | 193360.322 [116845.576, 269875.067] | 195212.755 [171106.575, 219318.934] | -1.56% | -0.95% | -0.61% | 1631/1085/1645 |
| Read Owner=Standalone&Policy=Default | 190069.846 [184226.598, 195913.094] | 190650.616 [183643.810, 197657.422] | 188731.103 [174211.297, 203250.908] | +0.31% | +1.02% | -0.70% | 0/0/0 |
| Read Owner=Standalone&Policy=Disabled | 190138.010 [186125.961, 194150.058] | 190501.674 [184453.533, 196549.815] | 189943.632 [184105.731, 195781.533] | +0.19% | +0.29% | -0.10% | 0/0/0 |
| Read Owner=Standalone&Policy=Enabled | 194024.775 [177133.173, 210916.377] | 195344.101 [163475.200, 227213.002] | 194240.940 [169841.602, 218640.277] | +0.68% | +0.57% | +0.11% | 1262/1066/1293 |

</details>

<details>
<summary>Run 38255514678: candidate managed retention by owner/policy (bytes)</summary>

| Owner / policy | Before connect | Connected idle | After churn | After dispose |
| --- | ---: | ---: | ---: | ---: |
| CacheProducer / Default | 137464 | 2010688 | 2042232 | 1748088 |
| CacheProducer / Disabled | 137464 | 2010856 | 2033392 | 1737448 |
| CacheProducer / Enabled | 137464 | 2025080 | 2043552 | 1753248 |
| Cluster / Default | 137448 | 2943848 | 2969976 | 3038224 |
| Cluster / Disabled | 137448 | 2943904 | 2970032 | 3040304 |
| Cluster / Enabled | 137448 | 2953416 | 2979128 | 3057672 |
| Function / Default | 137448 | 1848096 | 1874040 | 1589624 |
| Function / Disabled | 137456 | 1848160 | 1874104 | 1589688 |
| Function / Enabled | 137448 | 1852088 | 1883096 | 1572920 |
| Sentinel / Default | 143592 | 2784448 | 2798208 | 2420080 |
| Sentinel / Disabled | 137464 | 2784488 | 2798248 | 2420640 |
| Sentinel / Enabled | 137456 | 2660096 | 2740992 | 2368144 |
| Standalone / Default | 137464 | 1743088 | 1768896 | 1452264 |
| Standalone / Disabled | 137464 | 1743144 | 1768952 | 1482288 |
| Standalone / Enabled | 137464 | 1683536 | 1776984 | 1499848 |

</details>

<details>
<summary>Run 38264467525: every owner/policy interval, allocation and control drift</summary>

| Case |A ns [interval] | Candidate ns [interval] | B ns [interval] | Candidate/A | Candidate/B | B/A drift | Allocated B (A/candidate/B) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Read Owner=CacheProducer&Policy=Default | 72089.866 [70786.717, 73393.015] | 75315.422 [73390.696, 77240.149] | 69015.281 [67652.200, 70378.362] | +4.47% | +9.13% | -4.26% | 2649/2669/2667 |
| Read Owner=CacheProducer&Policy=Disabled | 74130.228 [71920.819, 76339.637] | 73127.377 [71323.981, 74930.773] | 70105.436 [68841.551, 71369.320] | -1.35% | +4.31% | -5.43% | 2670/2671/2666 |
| Read Owner=CacheProducer&Policy=Enabled | 75858.523 [74004.685, 77712.360] | 74007.408 [72619.140, 75395.675] | 69896.289 [68401.172, 71391.407] | -2.44% | +5.88% | -7.86% | 3954/3777/3957 |
| Read Owner=Cluster&Policy=Default | 69368.288 [67928.961, 70807.615] | 69826.133 [68833.800, 70818.466] | 66621.085 [65630.228, 67611.941] | +0.66% | +4.81% | -3.96% | 0/0/0 |
| Read Owner=Cluster&Policy=Disabled | 69132.116 [67780.543, 70483.688] | 70217.983 [69004.202, 71431.764] | 68600.990 [66590.446, 70611.533] | +1.57% | +2.36% | -0.77% | 0/0/0 |
| Read Owner=Cluster&Policy=Enabled | 70727.194 [69553.294, 71901.095] | 72066.677 [70710.812, 73422.543] | 70046.600 [68932.598, 71160.602] | +1.89% | +2.88% | -0.96% | 1749/1508/1742 |
| Read Owner=ClusterCacheProducer&Policy=Default | 73675.320 [71934.644, 75415.995] | 76728.401 [74633.012, 78823.791] | 71051.075 [69501.792, 72600.359] | +4.14% | +7.99% | -3.56% | 3289/3293/3292 |
| Read Owner=ClusterCacheProducer&Policy=Disabled | 74356.552 [73399.049, 75314.055] | 73083.744 [71360.210, 74807.277] | 72415.158 [69914.090, 74916.226] | -1.71% | +0.92% | -2.61% | 3289/3267/3266 |
| Read Owner=ClusterCacheProducer&Policy=Enabled | 74618.540 [72865.863, 76371.216] | 75362.817 [73749.772, 76975.863] | 72194.272 [69641.780, 74746.763] | +1.00% | +4.39% | -3.25% | 3280/4577/3274 |
| Read Owner=Function&Policy=Default | 73292.283 [72144.741, 74439.825] | 73034.606 [71017.330, 75051.881] | 72349.031 [71024.541, 73673.520] | -0.35% | +0.95% | -1.29% | 792/792/787 |
| Read Owner=Function&Policy=Disabled | 72134.880 [70932.451, 73337.308] | 73091.950 [71497.571, 74686.330] | 73992.648 [72780.760, 75204.535] | +1.33% | -1.22% | +2.58% | 792/778/772 |
| Read Owner=Function&Policy=Enabled | 74063.441 [71776.866, 76350.015] | 74096.171 [72605.416, 75586.927] | 74578.162 [73557.338, 75598.986] | +0.04% | -0.65% | +0.69% | 1918/1924/1915 |
| Read Owner=Sentinel&Policy=Default | 69642.139 [68424.839, 70859.439] | 67748.931 [66718.341, 68779.521] | 67800.958 [66978.612, 68623.304] | -2.72% | -0.08% | -2.64% | 0/0/0 |
| Read Owner=Sentinel&Policy=Disabled | 69107.057 [68115.742, 70098.371] | 68535.481 [67496.907, 69574.055] | 68724.879 [67435.016, 70014.742] | -0.83% | -0.28% | -0.55% | 0/0/0 |
| Read Owner=Sentinel&Policy=Enabled | 70223.004 [69154.626, 71291.382] | 68142.475 [66861.238, 69423.712] | 71182.622 [69956.739, 72408.505] | -2.96% | -4.27% | +1.37% | 1660/1111/1670 |
| Read Owner=Standalone&Policy=Default | 69308.905 [68729.829, 69887.981] | 67005.835 [66134.856, 67876.814] | 69555.350 [68545.281, 70565.419] | -3.32% | -3.67% | +0.36% | 0/0/0 |
| Read Owner=Standalone&Policy=Disabled | 67700.435 [66409.376, 68991.493] | 66999.693 [66381.637, 67617.749] | 70501.877 [69254.044, 71749.711] | -1.04% | -4.97% | +4.14% | 0/0/0 |
| Read Owner=Standalone&Policy=Enabled | 72454.343 [71311.317, 73597.368] | 67115.912 [65929.578, 68302.246] | 72341.978 [70873.325, 73810.631] | -7.37% | -7.22% | -0.16% | 1327/1107/1327 |

</details>

<details>
<summary>Run 38264467525: candidate managed retention by owner/policy (bytes)</summary>

| Owner / policy | Before connect | Connected idle | After churn | After dispose |
| --- | ---: | ---: | ---: | ---: |
| CacheProducer / Default | 137960 | 2011184 | 2035972 | 1744064 |
| CacheProducer / Disabled | 137960 | 2011240 | 2036044 | 1744504 |
| CacheProducer / Enabled | 137960 | 2021328 | 2044000 | 1741844 |
| Cluster / Default | 137944 | 2944344 | 2968256 | 3023728 |
| Cluster / Disabled | 137944 | 2944400 | 2968312 | 3038548 |
| Cluster / Enabled | 137944 | 2953896 | 2979624 | 3043188 |
| ClusterCacheProducer / Default | 137968 | 3205968 | 3230800 | 3287632 |
| ClusterCacheProducer / Disabled | 137976 | 3206016 | 3230864 | 3288320 |
| ClusterCacheProducer / Enabled | 137968 | 3216216 | 3238960 | 3314144 |
| Function / Default | 137944 | 1842448 | 1868260 | 1577108 |
| Function / Disabled | 137960 | 1848664 | 1872368 | 1575804 |
| Function / Enabled | 137952 | 1855176 | 1882344 | 1593200 |
| Sentinel / Default | 137952 | 2786544 | 2800580 | 2406744 |
| Sentinel / Disabled | 137960 | 2788732 | 2802788 | 2436292 |
| Sentinel / Enabled | 137952 | 2733020 | 2814356 | 2425628 |
| Standalone / Default | 137960 | 1743552 | 1767236 | 1467200 |
| Standalone / Disabled | 137960 | 1743640 | 1769448 | 1484856 |
| Standalone / Enabled | 137960 | 1677888 | 1774640 | 1485416 |

</details>

### Earlier explicit-request evidence: 12 October 2026

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

The original explicit-request comparison pins baseline `c4a9ee5c1c6e64341ee5678faf937e47a390f828`
and candidate merge `663301d0b2d5b3e7812467031ae664f734c57e81` (event head
`c4c6deb6babaebb87d64162fec3eb333fe4146d7`). Artifact
`command-retry-comparison-net10.0` (11745413087) was available on 14 October; its JSON
reports retain all 12 cases and 1080 actual workload samples across the three phases.
All candidate intervals overlap both controls; B/A drift is -0.78% to +2.28%. These
pre-pooling measurements do not quantify the current explicit-request path.

<details>
<summary>Earlier explicit requests: every interval, allocation and control drift</summary>

| Case |A ns [interval] | Candidate ns [interval] | B ns [interval] | Candidate/A | Candidate/B | B/A drift | Allocated B (A/candidate/B) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CatalogLength Policy=Default | 132806.469 [131706.526, 133906.413] | 133785.647 [132247.460, 135323.834] | 134110.891 [132569.565, 135652.216] | +0.74% | -0.24% | +0.98% | 776/774/772 |
| CatalogLength Policy=Disabled | 133117.136 [131516.936, 134717.335] | 134538.379 [133449.694, 135627.065] | 133372.649 [132366.198, 134379.100] | +1.07% | +0.87% | +0.19% | 767/778/775 |
| CatalogLength Policy=Enabled | 140005.371 [138916.633, 141094.108] | 140826.805 [139882.717, 141770.894] | 141067.688 [140061.871, 142073.505] | +0.59% | -0.17% | +0.76% | 2077/2067/2065 |
| GroupLength Policy=Default | 128132.153 [125762.134, 130502.172] | 131492.696 [130034.609, 132950.783] | 131055.040 [130262.199, 131847.881] | +2.62% | +0.33% | +2.28% | 0/0/0 |
| GroupLength Policy=Disabled | 130733.406 [129724.369, 131742.443] | 130598.000 [129597.526, 131598.474] | 131079.662 [130024.613, 132134.712] | -0.10% | -0.37% | +0.26% | 0/0/0 |
| GroupLength Policy=Enabled | 132596.643 [131518.304, 133674.981] | 130524.461 [128355.638, 132693.284] | 132841.430 [131457.593, 134225.267] | -1.56% | -1.74% | +0.18% | 0/0/0 |
| ScriptLength Policy=Default | 152494.667 [151038.770, 153950.565] | 154109.226 [153132.840, 155085.612] | 152881.213 [151603.248, 154159.177] | +1.06% | +0.80% | +0.25% | 1396/1393/1402 |
| ScriptLength Policy=Disabled | 152901.530 [151876.152, 153926.908] | 153828.683 [152349.912, 155307.454] | 153915.790 [152699.783, 155131.797] | +0.61% | -0.06% | +0.66% | 1392/1401/1366 |
| ScriptLength Policy=Enabled | 157716.235 [156557.399, 158875.071] | 158876.032 [157505.111, 160246.952] | 160259.455 [159459.579, 161059.331] | +0.74% | -0.86% | +1.61% | 2554/2554/2573 |
| TypedLength Policy=Default | 130648.963 [129290.761, 132007.165] | 131466.521 [129921.819, 133011.223] | 131968.725 [131172.295, 132765.154] | +0.63% | -0.38% | +1.01% | 0/0/0 |
| TypedLength Policy=Disabled | 131039.232 [130083.982, 131994.481] | 132259.420 [131260.606, 133258.234] | 131113.902 [129819.929, 132407.875] | +0.93% | +0.87% | +0.06% | 0/0/0 |
| TypedLength Policy=Enabled | 139022.875 [137339.582, 140706.168] | 137502.038 [136512.986, 138491.090] | 137936.974 [136357.390, 139516.558] | -1.09% | -0.32% | -0.78% | 1305/1308/1292 |

</details>
