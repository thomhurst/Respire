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

Replica read routing, streams, fire-and-forget operations, individual deferred batch/transaction
members (standalone and Sentinel batches retry only as complete units, described below), connection
protocol commands, cursor continuations, and
commands categorized as `Never` do not use a retry owner. Caller-supplied raw names
never acquire retry permission from their diagnostic name. Use an audited
`RespireCommands` descriptor for generated buffered commands, or declare a category.

Tracked Cluster cache reads share the producer's retry owner. Each attempt retains
its `CLIENT CACHING` prelude and routing-owned redirects. Recovery rebases the
cache read token before another attempt; coalesced waiters receive one producer
publication. Each waiter still owns its final caller-error observation.

The `respire.command.retries` counter records `respire.command.retry.category` and
`respire.command.retry.outcome`. On standalone and Sentinel clients, `attempted` counts
accepted resends. Cluster, script, function and failover-group owners have no transport
acceptance signal, so `attempted` counts started resends. `succeeded` and `exhausted`
report one terminal outcome per producer that made a resend.
Failed circuit admission does not invent an attempt. Coalesced waiters do not duplicate
producer retry events. Handled attempt failures and final caller errors retain their
separate error owners. Exporter exceptions cannot change retry decisions or replies.

Standalone and Sentinel batches use `RetryPolicy` to retry the complete pipeline.
A batch is not atomic: Redis can execute some or all members before a failure.
Replies remain pending until the retry decision is final, and only the final
attempt's results are published. A selected transient failure permits a resend
only when the policy permits every member's category. Without per-member write
evidence, starting any send makes the whole unit a possible execution; a connection
acquisition failure before any send is definitely unwritten. A successful `INCR`
alongside a failed `GET`, for example, prevents replay under the default read-only
policy. Retrying such a batch requires explicitly accepting accumulating effects.
Every retry resends every member in queue order, retains the original argument
bytes and cache mutation fence, and acquires circuit admission per command. One
deadline and caller cancellation token cover acquisition, admission, replies and
backoff. Retry telemetry counts accepted resends and one terminal outcome per
batch, using its most conservative member category. Hash import sessions and
server cursor continuations do not retry. Cluster batches and transactions retain
their existing behavior; further whole-unit work remains tracked by
[#1216](https://github.com/thomhurst/Respire/issues/1216).

## Correctness and performance evidence

The immediate-command implementation is available. Aggregate acceptance in
[#1215](https://github.com/thomhurst/Respire/issues/1215) and the retry epic
[#862](https://github.com/thomhurst/Respire/issues/862) remains open. Availability
does not establish zero overhead or complete resilience across every composition.

| Contract | Acceptance coverage |
| --- | --- |
| Category and transport evidence | `CommandRetryPolicyTests` checks the permission and failure matrix; `StandaloneCommandRetryTests` sends public catalog commands across every retryable category before a write. |
| Lost replies and effects | `StandaloneCommandRetryIntegrationTests` discards replies received from real Redis, then checks stored values, counters, checked-write results and configuration. Declared raw, script and function requests require explicit accumulating risk. |
| Topology ownership | `TopologyCommandRetryTests` checks discovery failures, Sentinel rediscovery, Cluster MOVED/ASK ownership and the newly active failover-group member. `TopologyCommandRetryIntegrationTests` loses executed increment replies, then checks effects independently after real Sentinel promotion, group switching and Cluster slot migration. Default risk preserves one effect; explicit accumulating risk permits two. |
| Deadline, cancellation and caching | Public Sentinel, Cluster, raw, script, function and group cases cancel after an observed failure. Sentinel/raw/script/function cases check the original deadline and late-reply FIFO retirement; existing Cluster cases bound redirects and retries by one deadline. Tracked Cluster tests cover cancellation, redirects, cache-token rebasing, coalesced publication and mutable payload reuse; standalone tests retain mutation fences through FIFO retirement. Groups have no command timeout: the caller token bounds the whole operation. |
| Metrics and reuse | Public paths repeat successful/exhausted retries and unselected errors with a throwing exporter. Tests distinguish internal attempt errors from final caller errors, producer retry outcomes from waiter observations, and group retry ownership from member final errors. `ImmediateRetryStateTests` checks snapshot isolation, cleanup, simultaneous rentals and canceled-frame retirement. |

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

### Completed owner and historical-default comparisons

[Owner-policy run 38250362487](https://github.com/thomhurst/Respire/actions/runs/38250362487)
and [historical-default run 38250366580](https://github.com/thomhurst/Respire/actions/runs/38250366580)
measure the identical fixture from [PR #1513](https://github.com/thomhurst/Respire/pull/1513).
The artifacts are `command-retry-owner-policies-net10.0` and
`command-retry-historical-default-net10.0`, respectively. Both preserve raw reports,
revision manifests, validation logs and full retention reports for A/candidate/B.
These completed measurements were assessed without rerunning unchanged code.

| Revision role | Immutable SHA |
| --- | --- |
| Owner-policy control A/B; both runs' event base | `14e4e9c22314377c92ec5aebe806bb5acdc28818` |
| Historical-default control A/B, before retry foundation | `15b7adf5090cce616d5fa395f96fb5f38d344ce1` |
| Both measured candidates (temporary PR merge) | `f0a4276a666e88f75a63d1c4cc1dd74d9df0a210` |
| Both measured PR heads | `e048419ae202125a64326718813c316000a760be` |

The owner comparison uses ShortRun (one launch, three measurement iterations).
The historical comparison uses MediumRun (two launches, ten warmups and fifteen
measurement iterations per launch). Each has two Dry fixture validations followed
by baseline A/candidate/baseline B. Dry output is not performance evidence.
All measured phases contain the expected fifteen or five distinct cases.

`Default` is a null policy; `Disabled` sets `MaxAttempts = 1`; `Enabled` uses an
enabled policy. Standalone, Sentinel and Cluster cases use healthy integer reads.
Function uses a declared read-only function. CacheProducer forces a standalone
cache miss with local clear and conversion on each invocation; it does not measure
cache hits or tracked Cluster cache retries. Sentinel and the single-primary
Cluster fixture establish healthy routing, not failover performance.

The tables preserve means and exported confidence intervals in **nanoseconds**,
allocations in **bytes per operation**, both brackets and control drift.
Mean ratios are point estimates, not confidence bounds.

<details>
<summary>Fifteen owner/policy latency and allocation cases</summary>

| Case | A ns [interval] | Candidate ns [interval] | B ns [interval] | Candidate/A | Candidate/B | B/A drift | Allocated B/op (A/candidate/B) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CacheProducer / Default | 110765.194 [93529.627, 128000.760] | 114532.638 [99296.788, 129768.487] | 113279.918 [95079.380, 131480.456] | +3.40% | +1.11% | +2.27% | 2654/2651/2648 |
| CacheProducer / Disabled | 112843.039 [103918.118, 121767.959] | 115249.424 [69013.826, 161485.022] | 110477.909 [94205.525, 126750.293] | +2.13% | +4.32% | -2.10% | 2649/2652/2663 |
| CacheProducer / Enabled | 117572.958 [95818.958, 139326.957] | 118218.144 [61064.346, 175371.942] | 114429.678 [31687.830, 197171.527] | +0.55% | +3.31% | -2.67% | 3908/3948/3964 |
| Cluster / Default | 98321.748 [68052.493, 128591.004] | 104529.458 [87888.651, 121170.265] | 91617.328 [68053.774, 115180.882] | +6.31% | +14.09% | -6.82% | 0/0/0 |
| Cluster / Disabled | 98419.202 [5788.133, 191050.270] | 105116.114 [-23999.667, 234231.896] | 99105.937 [72393.043, 125818.831] | +6.80% | +6.06% | +0.70% | 0/0/0 |
| Cluster / Enabled | 105844.386 [-80829.471, 292518.242] | 109288.173 [71496.543, 147079.803] | 109244.593 [89520.740, 128968.445] | +3.25% | +0.04% | +3.21% | 1729/1743/1745 |
| Function / Default | 113189.153 [97294.411, 129083.895] | 111374.825 [85986.566, 136763.085] | 117475.253 [22062.627, 212887.879] | -1.60% | -5.19% | +3.79% | 785/784/787 |
| Function / Disabled | 115105.549 [101430.495, 128780.603] | 110930.615 [85148.197, 136713.033] | 111915.443 [99250.477, 124580.409] | -3.63% | -0.88% | -2.77% | 783/771/786 |
| Function / Enabled | 115602.290 [99224.880, 131979.699] | 116470.665 [108152.447, 124788.882] | 117197.481 [59735.903, 174659.059] | +0.75% | -0.62% | +1.38% | 1895/1871/1868 |
| Sentinel / Default | 96160.265 [33064.581, 159255.949] | 100188.607 [63552.045, 136825.170] | 99005.604 [23234.148, 174777.060] | +4.19% | +1.19% | +2.96% | 0/0/0 |
| Sentinel / Disabled | 97312.804 [45166.431, 149459.176] | 101096.335 [79258.543, 122934.128] | 97602.057 [64584.087, 130620.028] | +3.89% | +3.58% | +0.30% | 0/0/0 |
| Sentinel / Enabled | 109227.829 [73566.981, 144888.677] | 109038.775 [77068.421, 141009.129] | 105236.923 [3016.488, 207457.357] | -0.17% | +3.61% | -3.65% | 1662/1667/1667 |
| Standalone / Default | 97219.153 [91134.177, 103304.130] | 99128.852 [94978.601, 103279.103] | 96312.773 [71603.730, 121021.815] | +1.96% | +2.92% | -0.93% | 0/0/0 |
| Standalone / Disabled | 98531.976 [41514.264, 155549.687] | 96342.271 [90260.853, 102423.689] | 99926.199 [84437.401, 115414.998] | -2.22% | -3.59% | +1.41% | 0/0/0 |
| Standalone / Enabled | 105351.533 [57544.478, 153158.589] | 106795.148 [34428.014, 179162.282] | 108403.786 [74934.257, 141873.315] | +1.37% | -1.48% | +2.90% | 1311/1321/1322 |

</details>

Every owner-policy candidate latency interval overlaps both control intervals.
Mean changes range from -3.63% to +6.80% against A and -5.19% to +14.09% against B;
B/A drift ranges from -6.82% to +3.79%. These broad ShortRun intervals do not
establish latency equivalence or zero overhead. Default/Disabled integer paths
allocate 0 B/op. Enabled costs 1321 B/op standalone, 1667 B/op Sentinel and
1743 B/op Cluster. Function Default/Disabled/Enabled costs 784/771/1871 B/op;
CacheProducer costs 2651/2652/3948 B/op, including the documented fixture work.
The controls have similar enabled costs. This run predates pooled retry frames.

| Case | A ns [interval] | Candidate ns [interval] | B ns [interval] | Candidate/A | Candidate/B | B/A drift | Allocated B/op (A/candidate/B) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CacheProducer / Default | 69636.721 [67975.733, 71297.709] | 69623.371 [67651.620, 71595.123] | 70445.283 [68990.620, 71899.947] | -0.02% | -1.17% | +1.16% | 2657/2669/2658 |
| Cluster / Default | 66777.916 [65483.422, 68072.410] | 69193.465 [68203.945, 70182.985] | 67737.068 [67036.070, 68438.065] | +3.62% | +2.15% | +1.44% | 0/0/0 |
| Function / Default | 69352.828 [68497.629, 70208.027] | 70754.107 [69682.958, 71825.255] | 69254.331 [68309.567, 70199.096] | +2.02% | +2.17% | -0.14% | 791/792/791 |
| Sentinel / Default | 67150.264 [66079.460, 68221.068] | 67481.921 [67068.758, 67895.083] | 67863.038 [67260.683, 68465.392] | +0.49% | -0.56% | +1.06% | 0/0/0 |
| Standalone / Default | 67601.438 [67151.955, 68050.921] | 67822.701 [66751.012, 68894.390] | 67750.449 [67087.692, 68413.206] | +0.33% | +0.11% | +0.22% | 0/0/0 |

Historical default integer paths allocate 0 B/op in every phase. Function costs
791/792/791 B/op (A/candidate/B); CacheProducer costs 2657/2669/2658 B/op.
Cluster candidate mean rises 3.62% against A and 2.15% against B, with 1.44% drift.
Its interval does not overlap A, but overlaps B. Other candidate intervals overlap
both controls. The Cluster signal requires attribution: this historical comparison
includes intervening changes and does not isolate retry dispatch as its cause.
Neither overlapping intervals nor zero integer allocations prove regression freedom.

<details>
<summary>Candidate four-stage retention, preserving every owner/policy</summary>

Each cell lists before-connect / connected-idle / after-churn / after-dispose
full-GC process snapshots in **bytes**. The owner-policy run has one process sample
per scope/stage; the historical run has two and reports their median. In every
listed scope POH size is 8184 / 24528 / 24528 / 24528 B, with zero reported POH
fragmentation. No policy or owner dimensions are pooled in these tables.
The linked artifacts and PR retain the same stages for both controls.

Owner-policy candidate:

| Owner / policy | Managed B: before / idle / churn / disposed | Working-set B: before / idle / churn / disposed |
| --- | ---: | ---: |
| CacheProducer / Default | 137760 / 2010984 / 2042528 / 1746888 | 46100480 / 66293760 / 67936256 / 95645696 |
| CacheProducer / Disabled | 137760 / 2015224 / 2037896 / 1745480 | 45932544 / 66158592 / 67780608 / 95600640 |
| CacheProducer / Enabled | 137760 / 2015952 / 2038568 / 1720144 | 45953024 / 66322432 / 67940352 / 99696640 |
| Cluster / Default | 137744 / 2944112 / 2970272 / 3038800 | 46215168 / 74571776 / 76189696 / 72577024 |
| Cluster / Disabled | 137744 / 2944168 / 2970328 / 3039160 | 46153728 / 74579968 / 76206080 / 79798272 |
| Cluster / Enabled | 137744 / 2948584 / 2974576 / 3052720 | 46075904 / 74715136 / 76353536 / 93429760 |
| Function / Default | 137744 / 1836104 / 1862048 / 1590440 | 46223360 / 65101824 / 66519040 / 80982016 |
| Function / Disabled | 137752 / 1848456 / 1874400 / 1591920 | 46161920 / 65007616 / 66445312 / 80818176 |
| Function / Enabled | 137744 / 1777896 / 1811504 / 1534808 | 45912064 / 64913408 / 66347008 / 94679040 |
| Sentinel / Default | 137744 / 2790824 / 2808816 / 2428696 | 46059520 / 74432512 / 76460032 / 82210816 |
| Sentinel / Disabled | 137760 / 2784880 / 2803208 / 2421744 | 46092288 / 74321920 / 76353536 / 81793024 |
| Sentinel / Enabled | 137752 / 2727680 / 2808616 / 2434232 | 46030848 / 74489856 / 76464128 / 105734144 |
| Standalone / Default | 137760 / 1743384 / 1764912 / 1478624 | 46227456 / 64270336 / 65617920 / 72855552 |
| Standalone / Disabled | 137760 / 1731152 / 1752680 / 1478496 | 46002176 / 64151552 / 65486848 / 72560640 |
| Standalone / Enabled | 137760 / 1666344 / 1762984 / 1495032 | 46096384 / 64319488 / 65634304 / 96432128 |

Historical-default candidate:

| Owner / policy | Managed B: before / idle / churn / disposed | Working-set B: before / idle / churn / disposed |
| --- | ---: | ---: |
| CacheProducer / Default | 137960 / 2011184 / 2035988 / 1728996 | 46082048 / 66220032 / 67837952 / 97417216 |
| Cluster / Default | 137944 / 2944344 / 2970380 / 3040552 | 45928448 / 74266624 / 75896832 / 74674176 |
| Function / Default | 137944 / 1836304 / 1860008 / 1590244 | 45887488 / 64806912 / 66232320 / 86372352 |
| Sentinel / Default | 137944 / 2789148 / 2805396 / 2411532 | 46104576 / 74407936 / 76443648 / 82055168 |
| Standalone / Default | 137952 / 1737432 / 1763176 / 1454292 | 45993984 / 64106496 / 65449984 / 72519680 |

</details>

Managed memory and working set remain above before-connect after disposal for
every owner in both runs and in both controls. Cluster managed bytes after disposal
also exceed after-churn in every phase. These process observations include runtime,
fixtures and pools. Sparse samples and working-set variation cannot attribute a leak,
establish complete reclamation, or measure per-command allocations.

### Pooled dispatch follow-up

[PR #1517](https://github.com/thomhurst/Respire/pull/1517) pools at most 256 retry
frame owners and avoids discovery for a healthy Sentinel generation. It clears and
releases frame arrays when the logical operation ends; transport copies retain their
FIFO ownership. Its isolated snapshot test measures 120000 bytes for 1000 old
snapshots, 0 bytes for 1000 pooled rentals and 64000 bytes for an escaping positive
control on net8.0/net10.0. This is snapshot ownership, not end-to-end allocation.
Remaining sampled allocations include attempt-local write evidence, declared
function arguments/results and cache fixture work; none becomes zero by assertion.

[Follow-up run 38255514678](https://github.com/thomhurst/Respire/actions/runs/38255514678)
was requested for head `7b5b4b5ec2b02b5b303efa718373256885c80768` and remains
unassessed in this audit. The pooling merge retained obsolete tracked-Cluster
constructor calls and failed local compilation; [#1519](https://github.com/thomhurst/Respire/pull/1519)
adapts that path to pooled ownership and returns its rental in `finally`. The earlier pinned
measurements above do not measure this repair or prove its end-to-end performance.

The audit extends `CommandRetryOwnerBenchmarks` with `ClusterCacheProducer`:
each invocation clears one local entry and performs a tracked Cluster read, with
verified cache miss/publication and result conversion. The current fixture has
eighteen owner/policy cases; default-only selection has six. The earlier completed
reports retain their original fifteen/five cases and standalone cache scope.
The `run-command-retry-audit-benchmarks` label requests a net10.0 MediumRun
A/candidate/B comparison, with the identical fixture and all owner/policy retention
dimensions. It pins compilable control `a19b9590235f6d2ad0f0e7d41dfafccbbb9b5394`,
before tracked retries and pooling; current main already contains those changes.
This control includes intervening changes and does not isolate their individual
costs. The new tracked case and the pooling repair need completed measurement
assessment; no asynchronous result is awaited or inferred here.

### Unmet aggregate acceptance

The audit establishes public-command category admission, lost-reply effects,
selected/unselected failures, discovery ownership, backoff cancellation, logical
deadlines, listener isolation, coalesced publication and repeated producer reuse
through the named suites. Real topology evidence now includes Sentinel promotion,
group member switching and Cluster key/slot migration. Cluster migration is not
replica-election evidence, and the tests do not prove every pooled lifetime race or
every possible API/topology/fault combination. Group operations remain bounded by
the caller token rather than a new group command timeout.

Performance acceptance remains unmet. The historical Cluster signal is not fully
attributed; the pooling follow-up still needs completed intervals, allocations and
retention assessment. End-to-end enabled paths remain positively allocated in the
completed evidence, and raw/catalog/script/function/cache paths cannot be advertised
as generally zero-allocation. Tracked Cluster cache retry overhead now has a fixture
but no assessed completed result. Process retention does not prove ownership leaks or
complete reclamation. A successful workflow validates reports, not acceptance.

Aggregate acceptance remains open in [#1499](https://github.com/thomhurst/Respire/issues/1499),
[#1215](https://github.com/thomhurst/Respire/issues/1215) and
[#862](https://github.com/thomhurst/Respire/issues/862). Cluster batch and transaction
whole-unit scope remains [#1216](https://github.com/thomhurst/Respire/issues/1216);
full circuit composition remains [#1256](https://github.com/thomhurst/Respire/issues/1256).
