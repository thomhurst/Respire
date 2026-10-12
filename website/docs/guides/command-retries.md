---
title: Command retry policy
---

Set `RespireOptions.RetryPolicy` to enable retries for buffered immediate commands
on standalone, Sentinel and Cluster clients. The default is `null`, which retains
native optimized dispatch and performs no automatic retries.

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

Public typed reads, conversions, and generated catalog commands share one retry
owner. Arguments are copied before asynchronous dispatch, so later caller memory
changes cannot change a resend. Each attempt consumes immutable transport write
evidence. One deadline and cancellation budget span connection selection, all
sends, and backoff. Cache mutation fences and coalesced cache producers retain
their logical ownership across all attempts and final conversion. Cancelled native
responses retain their fences until their FIFO reply slots retire.

Typed SET and ZADD refine their category only for audited option sets:

- `SET key value` and `SET key value XX` are `WriteLastWins`.
- `SET key value NX` is `WriteChecked`, the same as `SETNX`.
- `ZADD key XX` is `WriteLastWins`, and `ZADD key NX` is `WriteChecked`.

SET with an expiry, `KEEPTTL` or `GET`, ZADD with `INCR`, `CH`, `GT` or `LT`, and
XADD keep their conservative audited categories. A checked write checks its
condition again, which may prevent another write. If another caller removes the
key or member between attempts, `SET NX` or `ZADD NX` can write again. A replay
can report a different result, such as "not set" for a write that the first
attempt applied. An overwrite can replace an intervening update. Increasing
retry risk can repeat effects and cannot recover the original reply.

Generated catalog commands, such as `RespireCommands.String.SET`, do not inspect
their caller arguments. They use the descriptor's category for every argument
list. To allow a narrower category, declare it with `WithRetryCategory`.

## Topologies

Standalone and Sentinel clients share the owner that consumes transport write
evidence. Each Sentinel attempt resolves the current primary generation. A
`READONLY` reply retires that generation before the retry, so the retry discovers
and writes to the promoted primary. A lost primary connection also retires the
generation and triggers discovery.

Cluster attempts keep `MOVED`, `ASK`, retirement and `READONLY` recovery inside the
existing Cluster routing owner. A retry starts only after an attempt's final failure.
That routing does not expose transport write evidence, so every failed Cluster attempt
counts as possibly executed. Only the category permission can admit another attempt;
a definitely unwritten frame does not unlock categories above the policy maximum.
The logical deadline bounds every redirect inside every attempt. Redirects never
count as retry attempts.

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
Members keep their own `RetryPolicy`; a group retry starts only after a member's
operation fails. The group has no command timeout of its own. Each attempt uses its
member's timeout, and the caller's token bounds the whole operation.

## Policy settings

| Setting | Default | Bounds and meaning |
| --- | --- | --- |
| `MaxAttempts` | `3` | From 1 through 100, including the initial send. 1 allows no retry. |
| `MaxCommandRetryCategory` | `ReadOnly` | Maximum accepted risk after possible execution. `Never` disables retries. |
| `RetryableErrors` | `ConnectionLoss`, `Loading`, `TryAgain`, `MasterDown`, `ReadOnly` | Only these named flags are supported. `None` selects no failures. |
| `InitialBackoff` | 10 milliseconds | First retry's delay ceiling. Zero disables delay. |
| `MaxBackoff` | 1 second | At least `InitialBackoff`, at most 1 minute. |
| `Jitter` | `1` | Finite fraction from 0 through 1. 0 uses the ceiling; 1 randomizes from zero to the ceiling. |

The delay ceiling doubles after each completed attempt, capped at `MaxBackoff`.
With partial jitter, the delay ranges from `ceiling * (1 - Jitter)` to the ceiling.
Each retry must fit within the original logical deadline. The policy never starts
a new timeout budget. Cancellation stops retry admission. Dispatch must recheck
deadline and cancellation after backoff, immediately before another send.

## Execution risk

The audited categories form these explicit permission sets after possible execution:
`Always`; then `Connection`; then `ReadOnly`; then `WriteChecked`; then
`WriteLastWins`; then `WriteAccumulating`; then `ServerAdmin`. Selecting a category
permits it and the preceding named categories. Numeric enum comparisons are not
the contract. `Never` and invalid categories never authorize a retry.

A known command whose frame is **definitely unwritten** can be retried under any
enabled category selection. Both **unknown** write evidence and **possible
execution** require the category permission above. An exception's
`IsCommandNotSubmitted` flag, message, or nested cause does not upgrade transport
evidence. Unknown commands and scripts need an explicit caller category; recognizing
a command name does not grant permission.

Even a repeated read can return different data. A checked write can return a different
result after its first effect. An overwrite can destroy an intervening update.
Selecting `WriteAccumulating` or `ServerAdmin` explicitly accepts duplicate effects,
consumed data, and lost original results. Retrying never provides exactly-once
execution or recovers the original reply.

## Failures and dispatch scope

Eligibility requires both a selected failure and safe execution risk. Server replies
`LOADING`, `TRYAGAIN`, `MASTERDOWN`, and `READONLY` are individually selectable.
Authentication failures, exhausted connection recovery, protocol/configuration
errors, cancellation, and circuit admission rejection are ineligible. Arbitrary
server errors, including `BUSY` and `CLUSTERDOWN`, are not selected by this foundation.
`MOVED` and `ASK` remain owned by existing Cluster routing.

A circuit rejection never authorizes a resend to that endpoint, even though the
command was not submitted. A retry that waits for a replacement connection first
rejects an open circuit again. A `READONLY` retry requires dispatch to refresh its
primary route. Standalone dispatch selects the current multiplexer on every attempt,
and Sentinel dispatch selects the current generation. The full circuit and retry
composition, including half-open contention and failover switches, remains tracked by
[#1256](https://github.com/thomhurst/Respire/issues/1256).

Replica read routing, streams, fire-and-forget operations, deferred batch/transaction members, connection
protocol commands, cursor continuations, client-cache tracked reads on Cluster, and
commands categorized as `Never` do not use a retry owner. Caller-supplied raw names
never acquire retry permission from their diagnostic name. Use an audited
`RespireCommands` descriptor for generated buffered commands, or declare a category.

The `respire.command.retries` counter records `respire.command.retry.category` and
`respire.command.retry.outcome`. On standalone and Sentinel clients, `attempted` counts
accepted resends. Cluster, script, function and failover-group owners have no transport
acceptance signal, so `attempted` counts started resends. `succeeded` and `exhausted`
report one terminal outcome per producer that made a resend.
Failed circuit admission does not invent an attempt. Coalesced waiters do not duplicate
producer retry events. Handled attempt failures and final caller errors retain their
separate error owners. Exporter exceptions cannot change retry decisions or replies.

Deferred batches and transactions remain conservative: resending an
individual member can violate complete-unit ownership or atomicity. Whole-unit retry
work stays tracked by [#1216](https://github.com/thomhurst/Respire/issues/1216).

## Correctness and performance evidence

The immediate-command implementation is available. Aggregate acceptance in
[#1215](https://github.com/thomhurst/Respire/issues/1215) and the retry epic
[#862](https://github.com/thomhurst/Respire/issues/862) remains open. Availability
does not establish zero overhead or complete resilience across every composition.

| Contract | Acceptance coverage |
| --- | --- |
| Category and transport evidence | `CommandRetryPolicyTests` checks the permission and failure matrix; `StandaloneCommandRetryTests` sends public catalog commands across every retryable category before a write. |
| Lost replies and effects | `StandaloneCommandRetryIntegrationTests` discards replies received from real Redis, then checks stored values, counters, checked-write results and configuration. Declared raw, script and function requests require explicit accumulating risk. |
| Topology ownership | `TopologyCommandRetryTests` checks Sentinel rediscovery, Cluster MOVED/ASK ownership, the newly active failover-group member, and counter effects after a lost reply across all three owners. |
| Deadline, cancellation and caching | Standalone fault tests check backoff cancellation, one command deadline, coalesced producer publication and mutation fences through FIFO retirement. Cluster tests bound redirects and retries by one deadline. |
| Metrics and reuse | Standalone tests distinguish internal attempt errors from final errors. Topology tests repeat declared raw, script, function and Cluster operations, checking one terminal retry outcome despite a throwing exporter. |

Run correctness tests on both net8.0 and net10.0. Fake server tests exercise actual
public commands and controlled transport failures; the real Redis tests establish
server effects. A fake topology test alone does not prove real Redis failover behavior.

`CommandRetryBenchmarks` adds twelve healthy-path cases: typed and catalog reads,
declared scripts and failover-group operations, each with a null policy, a disabled
policy (`MaxAttempts = 1`) and an enabled policy. Inputs and group delegates are
prepared outside measurement. The `run-command-retry-benchmarks` PR label starts
a pinned net10.0 baseline/candidate/baseline comparison using the identical fixture
on both revisions. Reports include latency confidence intervals, allocations and
managed/POH/process retention before connection, at idle, after bounded churn and
after disposal. Retention snapshots describe the whole benchmark process, including
two client connections; they are not per-command allocation measurements.

### Completed policy comparison

[Run 38243810841](https://github.com/thomhurst/Respire/actions/runs/38243810841)
completed on 12 October 2026 for [PR #1506](https://github.com/thomhurst/Respire/pull/1506).
The `command-retry-comparison-net10.0` artifact contains the raw JSON reports,
phase logs, `revisions.log` and `comparison.md`. The measurements below preserve
that completed run; no benchmark was rerun for this assessment.

| Revision role | Immutable SHA |
| --- | --- |
| Control A and control B (candidate merge's first parent) | `c4a9ee5c1c6e64341ee5678faf937e47a390f828` |
| Measured candidate (temporary PR merge) | `663301d0b2d5b3e7812467031ae664f734c57e81` |
| Measured PR head (candidate merge's second parent) | `c4c6deb6babaebb87d64162fec3eb333fe4146d7` |
| Event base | `c4a9ee5c1c6e64341ee5678faf937e47a390f828` |

The event base happens to equal the control revision in this run; it is a separate
provenance field. PR #1506 subsequently merged head
`c5c9185f256c87079a107386083c573face7b083` as
`240f3ff43a1f0ebba32449caa7cf0e1ea9509c36`. Its later commit changes Cluster
replacement-connection waiting in `ClusterRouter.cs` and `RespireClient.CommandRetries.cs`.
These results measure the earlier pinned candidate, not the final merge or that
Cluster fix. The fixture itself is unchanged between the measured and final heads.

The runner used Ubuntu 24.04.5, AMD EPYC 9V74 (2 physical/4 logical cores),
.NET SDK 10.0.401, .NET 10.0.12 and BenchmarkDotNet 0.15.8 in Release/net10.0.
Redis ran as the workflow's pinned Redis 8.10 service. `MediumRun` used two
launches, ten warmups and fifteen measurement iterations per launch. The identical
candidate fixture was copied to the control. All twelve method/policy identities
were verified in both Dry validation reports and all three measurement reports;
each phase log starts with its expected pinned SHA. Dry results are fixture checks,
not latency evidence.

The four methods read the same length through typed `STRLEN`, catalog `STRLEN`,
a declared read-only script, or a failover-group delegate. `Default` means a null
policy, `Disabled` means `MaxAttempts = 1`, and `Enabled` uses the default retry
settings. Group policy applies to the group; its member's policy remains null.
Inputs and delegates are prepared outside measurement. These are healthy
round-trip operations, with no retry-triggering failure.

The table reports means and exported 99.9% confidence intervals in **nanoseconds**,
and managed allocations in **bytes per operation**. A and B bracket the candidate.
Candidate/A, candidate/B and B/A are changes in means, not confidence bounds on
those ratios.

| Case / policy | A ns [interval] | Candidate ns [interval] | B ns [interval] | Candidate/A | Candidate/B | B/A drift | Allocated B/op (A/candidate/B) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CatalogLength / Default | 132806.469 [131706.526, 133906.413] | 133785.647 [132247.460, 135323.834] | 134110.891 [132569.565, 135652.216] | +0.74% | -0.24% | +0.98% | 776/774/772 |
| CatalogLength / Disabled | 133117.136 [131516.936, 134717.335] | 134538.379 [133449.694, 135627.065] | 133372.649 [132366.198, 134379.100] | +1.07% | +0.87% | +0.19% | 767/778/775 |
| CatalogLength / Enabled | 140005.371 [138916.633, 141094.108] | 140826.805 [139882.717, 141770.894] | 141067.688 [140061.871, 142073.505] | +0.59% | -0.17% | +0.76% | 2077/2067/2065 |
| GroupLength / Default | 128132.153 [125762.134, 130502.172] | 131492.696 [130034.609, 132950.783] | 131055.040 [130262.199, 131847.881] | +2.62% | +0.33% | +2.28% | 0/0/0 |
| GroupLength / Disabled | 130733.406 [129724.369, 131742.443] | 130598.000 [129597.526, 131598.474] | 131079.662 [130024.613, 132134.712] | -0.10% | -0.37% | +0.26% | 0/0/0 |
| GroupLength / Enabled | 132596.643 [131518.304, 133674.981] | 130524.461 [128355.638, 132693.284] | 132841.430 [131457.593, 134225.267] | -1.56% | -1.74% | +0.18% | 0/0/0 |
| ScriptLength / Default | 152494.667 [151038.770, 153950.565] | 154109.226 [153132.840, 155085.612] | 152881.213 [151603.248, 154159.177] | +1.06% | +0.80% | +0.25% | 1396/1393/1402 |
| ScriptLength / Disabled | 152901.530 [151876.152, 153926.908] | 153828.683 [152349.912, 155307.454] | 153915.790 [152699.783, 155131.797] | +0.61% | -0.06% | +0.66% | 1392/1401/1366 |
| ScriptLength / Enabled | 157716.235 [156557.399, 158875.071] | 158876.032 [157505.111, 160246.952] | 160259.455 [159459.579, 161059.331] | +0.74% | -0.86% | +1.61% | 2554/2554/2573 |
| TypedLength / Default | 130648.963 [129290.761, 132007.165] | 131466.521 [129921.819, 133011.223] | 131968.725 [131172.295, 132765.154] | +0.63% | -0.38% | +1.01% | 0/0/0 |
| TypedLength / Disabled | 131039.232 [130083.982, 131994.481] | 132259.420 [131260.606, 133258.234] | 131113.902 [129819.929, 132407.875] | +0.93% | +0.87% | +0.06% | 0/0/0 |
| TypedLength / Enabled | 139022.875 [137339.582, 140706.168] | 137502.038 [136512.986, 138491.090] | 137936.974 [136357.390, 139516.558] | -1.09% | -0.32% | -0.78% | 1305/1308/1292 |

Control B/A drift ranges from -0.78% to +2.28%. Every candidate case's latency
interval overlaps both corresponding control intervals. This comparison therefore
does not show a latency separation between source revisions for these cases.
Both revisions already contain immediate retries: it is not a pre-retry regression
control, and overlapping intervals do not prove equivalence or zero cost.

Comparing modes within the candidate, enabled typed, catalog and script means are
respectively 4.59%, 5.26% and 3.09% above default, with non-overlapping intervals.
That enabled/default interval separation also appears in both controls. Enabled
typed dispatch allocates 1308 B/op; catalog and script dispatch allocate 2067 and
2554 B/op, respectively, versus 774 and 1393 B/op under default. The catalog and
script default allocations already exist in both controls and cannot be attributed
to adding retries by this run. Default/disabled typed and all group modes report
0 B/op. Default/disabled latency intervals overlap for each method in each phase.
Enabled group latency does not separate consistently from default across phases:
control A separates, while candidate and control B overlap. Candidate's -0.74%
enabled/default group mean difference is not evidence of an improvement.

### Whole-process retention

Each snapshot follows a forced compacting full GC. The fixture creates one client
and one single-member failover group (two client connections in total), then runs
256 sequential invocations of each of the four methods before `after-churn`.
`after-dispose` follows the timed benchmark and disposal of both clients. It is
not a snapshot immediately after only the bounded churn. The table gives medians
over 24 fixture-process snapshots per stage in each measured phase (eight per
policy), pooling methods and policy modes. Both Dry phases also have all four
stages, with twelve snapshots each, but are excluded from this measurement table.

All sizes below are **bytes**. POH size includes fragmentation; reported POH
fragmentation is zero in these samples. Working set measures the benchmark
process, not the Redis server.

| Phase | Stage | Samples | Managed bytes | POH bytes | POH fragmented bytes | Working-set bytes |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| A | before-connect | 24 | 138524 | 8184 | 0 | 52711424 |
| A | connected-idle | 24 | 2619300 | 24528 | 0 | 71794688 |
| A | after-churn | 24 | 2659268 | 24528 | 0 | 73783296 |
| A | after-dispose | 24 | 2373384 | 24528 | 0 | 86767616 |
| Candidate | before-connect | 24 | 138620 | 8184 | 0 | 52703232 |
| Candidate | connected-idle | 24 | 2619428 | 24528 | 0 | 71831552 |
| Candidate | after-churn | 24 | 2658196 | 24528 | 0 | 73832448 |
| Candidate | after-dispose | 24 | 2374228 | 24528 | 0 | 86847488 |
| B | before-connect | 24 | 138528 | 8184 | 0 | 52699136 |
| B | connected-idle | 24 | 2619348 | 24528 | 0 | 71800832 |
| B | after-churn | 24 | 2657864 | 24528 | 0 | 73807872 |
| B | after-dispose | 24 | 2376240 | 24528 | 0 | 86837248 |

Candidate managed memory rises by 38768 bytes between idle and bounded churn,
then falls to 2374228 bytes after disposal. It remains 2235608 bytes above the
before-connect median; POH remains 16344 bytes above its initial size. Working
set also remains higher, at 86847488 bytes after disposal versus 52703232 before
connection. Both controls show similar retention. These descriptive medians
include runtime, benchmark infrastructure and retained pools; they neither locate
an ownership leak nor demonstrate that all retained memory was reclaimed. They
are not per-command allocations and cannot establish zero-allocation dispatch.

### Unmet aggregate acceptance

This run supports measured zero allocation for default/disabled typed reads and
the tested group operations. It does not establish the full native/default
zero-allocation and no-measurable-cost requirement: catalog/script operations
allocate in all modes, the control is not pre-retry code, and only four healthy
paths are measured. Healthy enabled typed/catalog/script operations demonstrably
allocate and have higher latency than default in this fixture, so no general
zero-allocation or no-measurable-cost claim is made for enabled retries.

Enabled healthy Sentinel, Cluster, function and cache-producer paths remain
unmeasured here. Existing routing/cache comparisons do not isolate the incremental
cost of retry ownership. Real Redis topology failover effects, discovery failures,
the remaining cancellation/backoff/deadline combinations, internal/final error
ownership outside standalone coverage, and pool lifetime races still need the
corresponding composition evidence. A successful workflow validates complete
reports, not performance acceptance.

Aggregate acceptance remains open in [#1499](https://github.com/thomhurst/Respire/issues/1499),
[#1215](https://github.com/thomhurst/Respire/issues/1215) and
[#862](https://github.com/thomhurst/Respire/issues/862). Conservative deferred
batch/transaction scope remains [#1216](https://github.com/thomhurst/Respire/issues/1216);
full circuit composition remains [#1256](https://github.com/thomhurst/Respire/issues/1256).
