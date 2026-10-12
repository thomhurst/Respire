---
title: Transport write evidence
description: Write outcome ownership and the pinned transport performance measurements.
---

# Transport write evidence

Transport write evidence distinguishes an attempt that is definitely unwritten from one whose effect may have reached Redis. It does not enable automatic retries. An ambiguous or unknown outcome cannot authorize an unsafe resend, and an unwritten streaming frame does not make the caller's stream replayable.

## Ownership and retention limits

The foundation is internal. Ordinary public commands do not call `SendAttemptAsync` or `SendAskingStreamAttemptAsync`. Each of these opt-in owners allocates one non-pooled `CommandWriteObservation`. The observation contains one connection reference, two `long` frame coordinates and one `bool` for multiple frames. It holds no pending-response source, task token, payload array, stream, exception or collection of historical attempts. `RecordQueued` records coordinates under the write gate. A second frame sets the multiple-frame flag rather than adding another entry. The completed result copies only the outcome enum, normal response and optional `ExceptionDispatchInfo`; it does not expose or retain the observation itself.

This is a constant amount of observation state **per attempt**, not a global count or byte limit. The async send owner and its borrowed send frames can retain the observation while the operation is outstanding. The connection reference can retain the connection graph during that lifetime. Admission capacity does not bound owners waiting to enter the ring: `MaxInflightCommands` defaults to 16,384 replies per connection, but callers can create more waiting operations. There is no observation-specific timeout, eviction cap or fixed wall-clock retention limit. A cancelled owner can finish while its frame remains eligible for sending; freezing its outcome does not end actual transport ownership. Retaining an unconsumed `ValueTask`, response or exception can retain other owner/result state. These limits do not establish complete reclamation or a measured retained byte cost.

The source evidence is [the per-attempt owner and observation](https://github.com/thomhurst/Respire/blob/622972d400b96d111b18272ba011fd3edb6958f4/src/Respire/Networking/RespireConnection.WriteOutcomes.cs), [the result representation](https://github.com/thomhurst/Respire/blob/622972d400b96d111b18272ba011fd3edb6958f4/src/Respire/Networking/CommandAttemptResult.cs), and [the connection capacity default](https://github.com/thomhurst/Respire/blob/622972d400b96d111b18272ba011fd3edb6958f4/src/Respire/RespireOptions.cs). The existing `FrozenOutcomeSurvivesReuseOfTheExactSource` test verifies that recycling the exact pending-response source cannot alter an earlier completed outcome. That test establishes generation independence, not a heap-retention measurement.

Gathered SET uses a separate ownership barrier. Array-backed payloads of at least 8 KiB take `GatheredSetCommand`'s borrowed path. Its `GatheredSetWriteLease` has a pool retention capacity of 4,096 leases; that capacity is not an active-operation limit. One logical operation and every accepted write retain references. The response's `finally` waits for `FinishOperation`, and the sender releases each accepted write only after its send returns. Abort releases queued writes. Consuming the barrier's result returns the lease to the pool. The lease fields do not directly store the payload, but accepted write-buffer descriptors hold the caller array until write ownership ends. Cancellation, a deadline, a failed reply or a captured write outcome must not release that array early. See [the gathered command and lease](https://github.com/thomhurst/Respire/blob/622972d400b96d111b18272ba011fd3edb6958f4/src/Respire/Commands/GatheredSetCommand.cs) and [the sender](https://github.com/thomhurst/Respire/blob/622972d400b96d111b18272ba011fd3edb6958f4/src/Respire/Networking/RespireConnection.GatheredSet.cs).

## Aggregate child evidence audit: 2026-10-12 (#1481)

**Aggregate transport performance acceptance remains unmet.** [#1481](https://github.com/thomhurst/Respire/issues/1481) completes the audit of the merged completed-fill, residual stream/GET, batch and selection deliverables. It does not certify a passing transport comparison. [#1474](https://github.com/thomhurst/Respire/issues/1474), [#1214](https://github.com/thomhurst/Respire/issues/1214) and [#862](https://github.com/thomhurst/Respire/issues/862) remain open. Native child [#1520](https://github.com/thomhurst/Respire/issues/1520) owns the remaining corrected selection comparison, with verified dependencies on #1480 and this audit. Large non-streamed SET remains #976's separate scope.

### Pinned artifacts and coverage

This audit downloads the existing completed comparisons; it launches no new evidence and does not wait for asynchronous results. Each comparison uses one runner for its A/candidate/B bracket. Different runs and metric jobs are separate experiments; their absolute times and totals must not be subtracted to manufacture restoration or a cause. Earlier #1483 summaries of runs 38193605271 and 38197496226 remain historical targeted summaries, not substitutes for the latest comparison audited here.

| Deliverable / completed run | Disabled / enabled artifact ID | Baseline A and B | Measured candidate merge | Event head |
| --- | --- | --- | --- | --- |
| Batch #1479 / [38193859429](https://github.com/thomhurst/Respire/actions/runs/38193859429) | 11724425724 / 11724110941 | `e28016d4041c5ab417f6b6f6565c63a46ed8af56` | `815eec226f2e97fe7f8b28591950e73c4b881da2` | `9c04cc60fb3ca438cd833d279495e6733e210239` |
| Selection #1480 / [38195553203](https://github.com/thomhurst/Respire/actions/runs/38195553203) | 11723939593 / 11723884967 | `dfa7564f3946695f00c48f8d71679fdce935c8c2` | `fbc7af5a11404398455df510a2ddb93415d05c05` | `ba10a10c7132e891d1bd02431a97ba31cbd9d3e5` |
| Completed fills #1477 / [38201983679](https://github.com/thomhurst/Respire/actions/runs/38201983679) | 11727401522 / 11726499816 | `82a6fda724b1e79f95ac3eb778547586e286b4fc` | `f35ff1482dd5e5bcb05bf89eee71ecc61e6072a2` | `3c5b806bca77451f28ef8c151edebfdcdff1e392` |

All six artifacts were available and unexpired at download. Their revision files distinguish the event head from the measured merge. The latest fill comparison's library diff is confined to the reader; the remaining diff contains inspection metadata, tests and documentation. The normal public timing body still covers all 24 cases, including both upload sources, circuit-enabled GET, 32/100-command batches and both connection profiles. The separate stream diagnostics in #1478 do not alter those public bodies or require an unchanged comparison.

All runs report BenchmarkDotNet 0.15.8, Linux Ubuntu 24.04.5 x64, AMD EPYC 7763 with four logical CPUs, .NET 10.0.12 and SDK 10.0.401. These measurements do not cover net8.0, Windows or TLS performance. Each phase publishes the requested error-metric instrument. The benchmark children report permission denied when requesting high priority; this is retained as an experimental limitation.

The audit checks **144 case rows, 864 measured public launches, 12,960 raw actual workload samples, 12,470 retained result samples, 288 Dry launches and 4,608 full-GC samples**. The full-GC count includes both validation phases and all three measured phases. Every measured launch has 15 sequential actual iterations, positive durations, consistent operation counts, matching native GC operation counts and exit code zero. PID is bound to case, phase and launch by its native log; PID alone is not a durable cross-run identity. Tables below preserve both reported allocation columns and raw GC bytes/operations for both launches, every latency interval and control drift. The SHA-256 manifest and machine-readable audit remain at `C:/git/Respire-evidence/issue-1481` alongside the downloaded artifacts.

### Completed fills and residual public paths

The causal reader test removes seven repeated 80-byte successful fill tasks after the first fill on both supported runtimes: original boundary 560 bytes, changed boundary zero, positive control 560 bytes. That is isolated attribution, not a whole-upload promise. In the latest public comparison, every instant-upload candidate launch allocates less than every corresponding A/B launch. Reported A/candidate/B B/op are disabled **6452/5953/6477** and **6483/5914/6467**, enabled **6363/5979/6483** and **6518/5690/6387**, for one/two connections. This supports the removed allocation site without assigning the process-wide reduction to it exactly.

Latency does not uniformly improve. Disabled one-connection instant upload is +4.50%/+2.45%, with candidate interval 370.357–392.878 us separated above A's 361.582–368.763 us and overlapping B's 367.266–377.734 us. Enabled two-connection instant upload is faster than A with separated intervals and overlaps B; neither that overlap nor the other overlapping rows proves equivalence.

Delayed uploads remain mixed and required scope. Latest disabled/two-connection candidate latency is slower with separated intervals against both controls: 10133.532–10274.091 us versus A 10000.222–10118.057 and B 9741.874–9892.959 us (+1.44%/+3.94%, B/A drift -2.40%). Disabled/one-connection is separated above B. Enabled/two-connection is faster with separated intervals against both controls, but its exported allocation is 12759 B/op versus A/B 12547/12544. Disabled/one-connection candidate raw allocations 12734.750/12628.000 exceed every matching control launch; enabled/one-connection 12719.750/12774.625 also exceed every matching control. Background offload, delay tasks, cancellation waits and payload-copy suspension explain nonzero owners; they cannot assign these public launch deltas without matching traces.

Circuit-enabled GET remains mixed. Latest disabled report A/candidate/B is 18605/18522/17313 and 19814/19694/19027 B/op; enabled is 22690/24192/23382 and 22718/22095/23328. Enabled/one-connection raw candidate allocations 24140.023/24191.857 exceed all four control launches. Its aggregate latency intervals overlap both controls; overlap does not explain the allocation increase. The historical enabled/two-connection 220.570/266.072 us regime from run 38141963234 remains explicitly unresolved: its 223.319–263.323 us interval is slower and separated from A's 213.105–221.602 us, despite overlapping B. Neither the new lower GET means nor unrelated diagnostic processes retrospectively identify that regime's cause.

The latest disabled one-connection/100 batch report is 32398/32414/32408 B/op; both candidate launches 32408.398/32414.488 exceed every A launch, and the second exceeds every B launch. Enabled/two-connection/100 latency is +4.83%/+2.00%, separated above A, with control drift +2.77%. The historical enabled/one-connection/100 30015.758/30016.531 B/op versus controls 30013.080–30014.564, and the disabled/two-connection second-launch drop, remain unattributed. Different baselines and process counters prevent subtraction of those older totals from the new 32,000-byte rows.

Other public paths are not omitted. The complete tables retain PING, server error, small/large SET, raw/public pipeline and all batch rows, including separated slow intervals. Examples include latest disabled SmallSet/two connections separated above both controls (+1.20%/+1.35%); batch-diagnostic run enabled LargeSet/two connections separated above both (+6.36%/+4.72%); and selection run enabled one-connection/100 batch separated above both (+6.27%/+5.15%). Unchanged timed call graphs exclude a direct call to the modified streaming helper in batches and selection, but do not make their unexplained launch differences harmless noise. No sampled control establishes a focused production defect behind these timing regimes, so this audit changes no transport implementation and requests no unchanged rerun.

### Allocation stacks and suspension controls

The two batch-instrumented comparisons contain **96 separate diagnostic launches and 145,681 allocation samples**, with zero lost events, zero missing types and **18 missing stacks**. Every revision/case/phase/mode/launch tuple is present exactly once. Identity metadata matches the trace header; samples carry its process ID. All held reply and pool-demand controls are incomplete before release. Both frameworks' original local batch evidence remains the causal validation of the construction, fixture and suspension boundaries; these new Linux processes confirm the sampled owners against independently pinned libraries.

Samples identify `RespirePending<long>` and `Op<IncrementCommand,long>` under construction, list expansion as `Op[]`, the public `StandaloneBatch` Task box, `TryExecuteAsync` completion boxes, and pooled `CompleteSendAsync`/`RunStandaloneAsync` demand misses. Held replies distinguish actual suspension from construction. The pooled wrapper removes only its own fixture Task boundary; it does not erase transport completion owners. Idle and background-positive controls retain collector/background work, including worker `byte[]` stacks. Samples with missing stacks cannot attribute a stack. Allocation intervals and sample counts are not exact bytes by object type and cannot explain a historical fractional B/op delta.

The residual stream evidence is independently rechecked against its metadata/trace hashes: **48 processes, 5,917 samples, zero lost events, zero missing types and three missing stacks**, at revision `6412839d802d87d4e0ef48748832ac2b3db9f156`. The two frameworks each contain all case/profile/mode/launch identities. Its actual-reader identity check, derived-reader suspension, linked cancellation, cancellable/non-cancellable write waits, held payload writes/GET receives, public Task/pooled ValueTask fixtures and overlapping final-wait owners retain the causal boundaries described below. Sampled payload-copy boxes, cancellation promises, derived fills, pooled final-wait demand misses and GET pending/circuit/read owners are separate from reused completed fill tasks. These Windows diagnostic PIDs are not the historical Linux public launch PIDs. Neither sample counts nor process allocation totals establish an isolated whole-upload cost or retrospectively assign mixed public deltas.

### Selection code, scheduling and evidence gap

#### Corrected selection comparison request (#1520)

The reviewed aggregate audit closes #1481, so #1520 can request the corrected control experiment. This request pins the library baseline to the parent of the merged #1488 corrections, and the candidate and shared fixture to that merge. The production selection source is identical across these library revisions; the experiment measures the corrected capture and causal controls, not a selection optimization. Both bracket controls use the same corrected fixture. The earlier run's invalid affinity records remain invalid.

<!-- selection-comparison-request -->
```json
{
  "issue": 1520,
  "baseline": "82a6fda724b1e79f95ac3eb778547586e286b4fc",
  "candidate": "e3a19cf86dbcb94ad616ebabc8eb5b928f5c4946",
  "fixture": "e3a19cf86dbcb94ad616ebabc8eb5b928f5c4946"
}
```

The label event captures this request from its immutable PR merge, verifies the PR head, and runs baseline/candidate/baseline on one runner per metric mode. `revisions.log`, `selection-request.log` and the three `.commit` files bind the request source, library revisions, copied fixture revision and SHA-256 hashes of every copied input, production multiplexer source and provenance gate. The gate is unchanged from the merged corrections. A request applies only when its JSON differs from the PR merge's first parent; later PRs that inherit this historical request retain the ordinary merge/first-parent comparison.

The request retains every public Medium timing body, two launches, both connection profiles, and the separate Short reference, tiering-off, pinned and no-subtraction controls with unroll factor 16. Linux pinned children use the merged pre-runtime `taskset` launcher. Request `run-transport-benchmarks` once after this request is reviewable. A later pass must verify every public and diagnostic selection process in both metric modes with the unchanged gate, then audit both launches against A and B, B/A drift, raw overhead/workload, native addresses, code/tier versions and scheduling at both boundaries. Do not rerun unchanged evidence for a favorable result.

#### Corrected selection comparison audit: run 38266422052

[Run 38266422052](https://github.com/thomhurst/Respire/actions/runs/38266422052) completed both metric modes for request source `36a31d5f175153e6f943d9366b49f7c1beea5b95` and PR head `adfdbbe729352951c6c12f4087593bf3a18414fc`. Artifacts are `transport-acceptance-comparison-enabled` (ID 11758303250) and `transport-acceptance-comparison-disabled` (ID 11758272809). Both record baseline library `82a6fda724b1e79f95ac3eb778547586e286b4fc` and candidate/fixture `e3a19cf86dbcb94ad616ebabc8eb5b928f5c4946`. Every copied fixture input hashes identically across baseline, candidate and fixture. The production multiplexer source hashes identically across the library pair (`df91238d…`), so this bracket compares the corrected controls over unchanged selection source. The enabled leg ran on an Intel Xeon Platinum 8370C runner and the disabled leg on an AMD EPYC 7763 runner, both with four logical CPUs, SDK 10.0.401 and runtime 10.0.12. The two modes are separate experiments: their absolute values and code bytes do not measure a metric cost.

**Provenance gate: passes.** The unchanged `VerifySelectionEvidence.py` (Git blob SHA-256 `574160c4…`, matching the recorded fixture gate) was rerun offline over each downloaded artifact. It verifies **64 selection processes per mode, 128 in total**: four Dry validation, twelve public Medium and 48 diagnostic processes per mode. The regenerated `selection-launches.json` is identical to the CI copy. Each process binds revision, fixture revision, phase, metric mode, purpose, PID, case and launch at both boundaries. It also has its own JIT listing with selection code and tier labels, a perf map containing `SelectConnection`, Linux process and per-task `stat`/`schedstat`/`sched`/`status` at both boundaries, and raw workload samples. Overhead samples are present except in no-subtraction and Dry processes, as designed. Every diagnostic case has unroll factor 16, and every tiering-off process stamps `DOTNET_TieredCompilation=0`. All **24 pinned launches** request `affinityMask` 1, which is CPU 0, the lowest CPU allowed to the runner. The pinned fixture's pre-runtime `/usr/bin/taskset` launcher source hashes identically in all three checkouts. In every pinned launch, the process and all 14 captured tasks report `Cpus_allowed_list: 0` at both boundaries. This clears the `Missing affinity target` rejection of run 38195553203.

Public Medium selection, in ns. A/C/B are report means; per-launch values are mean raw workload minus mean raw overhead:

| Mode | Connections | A | C | B | C/A | C/B | B/A | Launches A; C; B |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| enabled | 1 | 1.936 | 1.947 | 1.924 | +0.61% overlap | +1.23% overlap | -0.61% overlap | 1.920/1.953; 1.943/1.957; 1.951/1.902 |
| enabled | 2 | 10.522 | 10.153 | 13.745 | -3.51% overlap | -26.13% separated | +30.63% separated | 10.912/10.200; 9.896/10.462; 17.912/9.931 |
| disabled | 1 | 2.643 | 2.972 | 2.643 | +12.47% separated | +12.47% separated | +0.00% overlap | 2.641/2.645; 3.314/2.642; 2.644/2.637 |
| disabled | 2 | 6.386 | 6.408 | 6.402 | +0.34% overlap | +0.10% overlap | +0.24% overlap | 6.405/6.391; 6.389/6.414; 6.409/6.429 |

Each separated row comes from one launch: enabled two-connection B launch 1, and disabled one-connection candidate launch 1. Neither involves a production source change. Each slow launch has the same normalized Tier1 `SelectConnection` instruction sequence as the fast launches of its mode and profile. That sequence is shared by every tiered selection launch of the same mode and profile, including the slow diagnostic launches below; the one exception is noted under pinned. `GetConnection` has two or three normalized Tier1 variants per profile, but they do not track slow launches: all six enabled two-connection public launches share one variant. Captured code shows which versions were generated, not which version ran each timed invocation.

Diagnostic controls, A/C/B report means in ns:

| Control | Enabled, 1 | Enabled, 2 | Disabled, 1 | Disabled, 2 |
| --- | --- | --- | --- | --- |
| reference | 1.926/2.167/1.913 | 10.226/10.149/10.231 | 2.677/2.664/2.666 | 6.426/6.409/6.429 |
| tiering-off | 4.282/4.253/4.303 | 11.591/11.385/10.693 | 4.267/4.301/6.511 | 6.936/7.023/6.889 |
| pinned | 6.733/1.962/6.507 | 17.329/16.160/22.757 | 2.698/7.498/7.587 | 29.040/18.025/6.423 |
| no-subtraction (raw) | 5.929/5.786/5.766 | 17.926/18.010/14.868 | 5.974/6.007/5.987 | 9.759/9.813/12.835 |

- **Reference.** Disabled-mode A/C/B means agree within 0.5% for both profiles. The enabled one-connection candidate is +12.52%/+13.24% with overlapping intervals. That comes from one 6.413 ns raw launch; the other five enabled one-connection reference launches are 5.906–5.928 ns raw.
- **Tiering-off.** FullOpts-only one-connection selection is about 4.2–4.3 ns net, compared with about 1.9–2.7 ns for tiered Tier1, so dynamic tiering/PGO shapes the measured cost. Two isolated slow launches remain: enabled A two-connection launch 1 (16.648 versus 14.535 ns raw) and disabled B one-connection launch 2 (11.857 versus 7.794 ns raw). Their FullOpts code matches the other launches.
- **Pinned.** Pinning increases launch regimes instead of removing them. **11 of 24** pinned launches run at roughly 1.8–5 times their profile's fast raw workload: six enabled and five disabled, spread across A, C and B. All tasks share CPU 0. Slow pinned launches record 848–1,611 nonvoluntary context switches between boundaries, all pinned launches 321–1,611, and reference launches 15–297. Background runtime and transport work competes with the timed thread. Enabled A one-connection launch 1 (15.412 ns raw) captured only Tier0 `SelectConnection`/`GetConnection`/`GetSingleConnection` bodies. That is consistent with tier-up delayed by single-CPU contention, but does not prove it. The other ten slow pinned launches captured the same Tier1 code as their fast neighbours. Disabled B one-connection launch 2 also has an inflated overhead estimate: 12.450 ns over 20 overhead samples, against about 3.33 ns elsewhere.
- **No-subtraction.** One-connection raw workload is 5.6–6.0 ns on both runners. Overhead estimates are about 4.00 ns on the enabled runner and 3.33 ns on the disabled runner, so subtraction removes 55–68% of the raw one-connection measurement. The overhead estimate dominates the reported one-connection results. Two-connection raw workload adds the round-robin `Interlocked.Increment`: about 13.9–14.9 ns on the Intel runner and 9.7–9.8 ns on the AMD runner. Outlier launches are excluded from those ranges only: 21.922/21.203/15.803 ns enabled and 15.827 ns disabled.

Scheduling and frequency limits: per-task context-switch deltas cover only tasks alive at both boundaries. Some public deltas are negative because tasks exited between boundaries, so they are not process totals. On the enabled runner `scaling_cur_freq` is usable at all 128 boundaries, at 2,320,267–4,465,658 kHz. On the disabled runner it is unavailable at all 128 boundaries because the cpufreq path is absent. Boundary snapshots do not cover an iteration and do not attribute any launch regime.

**Selection disposition: provenance positive; selection timing negative as acceptance evidence; no production defect.** This pinned Linux comparison validates the corrected capture and causal controls: every public and diagnostic process passes the unchanged gate in both modes. With identical production selection source, it still shows launch-level separated differences: +12.47% against both controls for disabled one connection, and +30.63% B/A drift for enabled two connections. Pinned, tiering-off and no-subtraction launches are bimodal with matching normalized selection code. Single-launch selection regimes of this size therefore occur without a production change and cannot alone establish a selection regression or improvement. Overlapping rows do not prove equivalence either. No control isolates a focused production defect, so no implementation child is filed. One connection skips the round-robin atomic increment; two connections use it.

The historical enabled two-connection 9.798/8.538 ns and disabled one-connection +17.80%/+18.49% regimes come from earlier processes without matching launch-bound evidence, so they remain unattributed. This run shows that similar shapes can arise from identical source, but it does not explain those processes. No unfavorable launch is excluded and no historical total is subtracted.

This closes only the #1520 selection evidence gap. It does not clear the other public latency/allocation gaps from #1481. #1474, #1214 and #862 remain open until full aggregate acceptance is established; #976 remains separate.

Run 38195553203 contains **128 selection processes**: 32 Dry/public processes and 96 separate diagnostic processes across both metric modes and three bracket phases. Every process has matching before/after identity, JIT code/tier output, perf-map addresses, scheduling snapshots and raw workload samples. Required overhead samples are present; no-subtraction and Dry controls omit them as designed. Diagnostic unroll factor is explicitly 16. Tiering-off controls stamp `DOTNET_TieredCompilation=0`. These checks establish capture provenance, not which tier ran throughout a timed iteration or a causal layout effect.

The current full provenance gate rejects all six diagnostic phase/mode combinations with **`Missing affinity target`**. All **24 pinned launches** lack the later `affinityMask` identity. Their captured process/task masks are singleton masks, but the missing requested target prevents verification against the corrected control, and this run predates the pre-runtime `taskset` correction. Do not weaken the current verifier to accept the old artifacts. The remaining historical records were audited separately without treating affinity as verified. Local corrected captures at `a88a40e80fc397073e5bec48f6d845f999c76dd8` pass the current gate for 32 Linux diagnostic processes; shortened local sampling validates instrumentation, not the required pinned acceptance comparison. #1520 owns that specific remaining evidence gap and reuses the merged corrections.

Frequency snapshots are absent in all 64 disabled-mode captures and present in all 64 enabled-mode captures. Enabled candidate public selection snapshots span 2,300,000–3,026,946 kHz only as recorded per launch; the exact per-launch ranges are retained in the machine-readable audit. Snapshot variation cannot establish frequency across an iteration or attribute a timing regime. No comparison of absolute means between these separate metric jobs identifies an instrumentation cost.

Candidate public one-connection selection launches are 1.000877/1.295964 ns disabled and 1.935580/2.039203 ns enabled; two-connection launches are 6.833738/7.067786 and 10.504373/10.556460 ns. Captures include Tier0/Tier1/FullOpts listings. In disabled mode the one-connection aggregate is +14.11%/+13.79% and separated above both controls; the two-connection enabled aggregate is separated above A and separated below B. The older batch run's disabled/two-connection launches 17.567/6.419 ns and +78.49%/+86.58% also remain visible. The historical 9.798/8.538 ns and +17.80%/+18.49% regimes lack launch-bound historical code/scheduling controls and remain unattributed.

The selection method calls the connection multiplexer: one connection bypasses the round-robin atomic increment, while two connections execute it. Neither path calls the streamed final-frame wait. New code/tier captures and separate affinity/tiering/subtraction controls do not prove a streaming effect, causally attribute the older regimes or remove observer effects. Both adjusted results and raw overhead/workload records remain in the artifact and audit archive.

### Retention and disposition

Every full-GC sample retains **5,767,168 bytes of caller arrays**; process memory includes clients, pools, collector, runtime and background work. The tables retain before-connect, connected, after-churn and after-dispose ranges for each connection profile and phase, including working-set/private-memory growth and nonzero post-dispose POH values. They cannot prove isolated transport retention, complete reclamation or a global observation cap. The constant state per attempt, admission-waiter limits, unfinished write ownership and gathered lease barriers at the top of this guide remain the applicable contracts.

This is a completed **negative aggregate disposition**. The fill-task cause is established locally and supported by both public launch allocations; residual stream and batch owners are identified with explicit sampling limits. Historical attribution, separated public timing rows, mixed allocation rows and the corrected selection comparison remain unestablished. #1474 must not close on this audit or on a successful workflow alone. #1214 and #862 remain open until their full acceptance is established; #976 remains separate. No defaults, ownership limits, FIFO, cancellation, source generation, original exceptions or mutation barriers change here.

### Complete public evidence

Positive deltas mean slower. All intervals/launch means below use microseconds, including the internal selection rows; allocation is B/op. Batch operations are whole batches, pipeline operations are individual commands. Exported allocation uses the last launch's GC statistics; raw ratios show each launch separately. Retained overhead-adjusted result samples are not independent repetitions of the whole bracket. Confidence interval overlap proves neither equivalence nor zero cost. Each expandable table preserves all public rows, native process identities and retention groups for its pinned artifact.

<details>
<summary>Every aggregate interval separated above a control</summary>

| Run / error metrics | Case | Separated slower than | Candidate/A; candidate/B |
| --- | --- | --- | ---: |
| 38193859429 / disabled | ClientPing / Connections=2 | A | +1.08%; -0.18% |
| 38193859429 / disabled | SelectConnection / Connections=2 | A, B | +78.49%; +86.58% |
| 38193859429 / disabled | StandaloneBatch / Connections=1&count=100 | A | +1.19%; +1.08% |
| 38193859429 / enabled | ClientServerError / Connections=1 | A, B | +1.42%; +1.30% |
| 38193859429 / enabled | LargeSet / Connections=2 | A, B | +6.36%; +4.72% |
| 38193859429 / enabled | StandaloneBatch / Connections=2&count=32 | B | +0.48%; +0.67% |
| 38195553203 / disabled | SelectConnection / Connections=1 | A, B | +14.11%; +13.79% |
| 38195553203 / disabled | StandaloneBatch / Connections=1&count=32 | A | +3.97%; +1.57% |
| 38195553203 / enabled | ClientPing / Connections=1 | A | +3.40%; +0.86% |
| 38195553203 / enabled | ClientServerError / Connections=1 | A | +2.53%; -0.41% |
| 38195553203 / enabled | LargeSet / Connections=1 | A | +6.29%; +3.31% |
| 38195553203 / enabled | Pipeline / Connections=1 | B | +0.80%; +1.36% |
| 38195553203 / enabled | Pipeline / Connections=2 | A | +6.71%; +2.97% |
| 38195553203 / enabled | PublicPipeline / Connections=1 | A | +2.13%; +0.53% |
| 38195553203 / enabled | PublicPipeline / Connections=2 | A | +3.64%; +3.01% |
| 38195553203 / enabled | SelectConnection / Connections=2 | A | +1.97%; -1.79% |
| 38195553203 / enabled | StandaloneBatch / Connections=1&count=100 | A, B | +6.27%; +5.15% |
| 38195553203 / enabled | StandaloneBatch / Connections=1&count=32 | A | +1.84%; +0.47% |
| 38195553203 / enabled | StandaloneBatch / Connections=2&count=32 | A | +1.53%; -0.96% |
| 38201983679 / disabled | ClientServerError / Connections=2 | A | +1.24%; +0.55% |
| 38201983679 / disabled | PublicPipeline / Connections=2 | B | +1.04%; +1.55% |
| 38201983679 / disabled | SmallSet / Connections=2 | A, B | +1.20%; +1.35% |
| 38201983679 / disabled | StandaloneBatch / Connections=2&count=100 | B | +0.27%; +1.03% |
| 38201983679 / disabled | StandaloneBatch / Connections=2&count=32 | B | +0.22%; +0.88% |
| 38201983679 / disabled | StreamedSetWithDelayedSource / Connections=1 | B | +0.64%; +2.67% |
| 38201983679 / disabled | StreamedSetWithDelayedSource / Connections=2 | A, B | +1.44%; +3.94% |
| 38201983679 / disabled | StreamedSetWithInstantSource / Connections=1 | A | +4.50%; +2.45% |
| 38201983679 / enabled | LargeSet / Connections=1 | B | -5.29%; +3.30% |
| 38201983679 / enabled | SmallSet / Connections=2 | B | -0.56%; +2.40% |
| 38201983679 / enabled | StandaloneBatch / Connections=2&count=100 | A | +4.83%; +2.00% |

</details>

### Run 38193859429: error metrics disabled

Baseline `e28016d4041c5ab417f6b6f6565c63a46ed8af56`; measured candidate `815eec226f2e97fe7f8b28591950e73c4b881da2`; event head `9c04cc60fb3ca438cd833d279495e6733e210239`.

<details>
<summary>Every public interval, allocation row, launch and retention group</summary>

| Case / parameters | Candidate/A; candidate/B | B/A drift | 99.9% interval us: A / candidate / B | Reported B/op: A / candidate / B |
| --- | ---: | ---: | --- | --- |
| CircuitStreamedGet / Connections=1 | -0.40%; -1.45% | +1.07% | 386.377253–394.912471 / 386.072746–392.105352 / 390.015950–399.638056 | 19012 / 18778 / 17834 |
| CircuitStreamedGet / Connections=2 | +1.67%; +1.14% | +0.52% | 376.080866–384.853713 / 380.281663–393.322557 / 376.475965–388.410805 | 20941 / 19691 / 18878 |
| ClientPing / Connections=1 | -0.27%; -1.18% | +0.92% | 190.404492–192.457096 / 189.736253–192.085143 / 192.211477–194.168556 | 184 / 184 / 184 |
| ClientPing / Connections=2 | +1.08%; -0.18% | +1.26% | 190.589716–192.535259 / 192.998133–194.256958 / 193.345385–194.623868 | 184 / 184 / 183 |
| ClientServerError / Connections=1 | -0.16%; -1.52% | +1.38% | 221.352509–224.421554 / 221.420864–223.635229 / 224.806989–227.097154 | 952 / 952 / 952 |
| ClientServerError / Connections=2 | +1.00%; -0.48% | +1.48% | 221.792444–223.748246 / 223.574829–226.400354 / 224.490941–227.664537 | 953 / 952 / 952 |
| LargeSet / Connections=1 | +1.67%; +0.19% | +1.47% | 1744.382622–1851.823105 / 1780.037934–1876.143779 / 1771.058681–1878.072171 | 176 / 176 / 176 |
| LargeSet / Connections=2 | +1.30%; -2.37% | +3.76% | 1744.847032–1826.842099 / 1762.467853–1855.749981 / 1780.262978–1925.778392 | 176 / 176 / 180 |
| Pipeline / Connections=1 | -0.42%; -0.97% | +0.56% | 6.709123–6.772011 / 6.692247–6.732681 / 6.754328–6.801646 | 7 / 7 / 7 |
| Pipeline / Connections=2 | +0.86%; -0.97% | +1.84% | 7.644128–7.743327 / 7.692868–7.826265 / 7.775335–7.895807 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | +0.45%; -0.06% | +0.51% | 6.733065–6.786784 / 6.765444–6.814738 / 6.772020–6.816639 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | -0.83%; -1.43% | +0.61% | 7.831569–7.942054 / 7.744635–7.898769 / 7.880324–7.989748 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | -0.86%; -0.27% | -0.59% | 0.002646–0.002675 / 0.002632–0.002644 / 0.002634–0.002656 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | +78.49%; +86.58% | -4.34% | 0.006475–0.006963 / 0.008035–0.015951 / 0.006397–0.006459 | 0 / 0 / 0 |
| SmallSet / Connections=1 | -0.23%; -0.60% | +0.37% | 194.843438–196.932204 / 194.639267–196.219475 / 195.830646–197.389475 | 176 / 176 / 175 |
| SmallSet / Connections=2 | -0.13%; -1.40% | +1.29% | 195.593987–198.031397 / 195.844392–197.264945 / 198.897503–199.798045 | 176 / 176 / 176 |
| StandaloneBatch / Connections=1&count=100 | +1.19%; +1.08% | +0.11% | 358.560057–362.030690 / 362.438160–366.699190 / 358.207005–363.164782 | 32407 / 32410 / 32413 |
| StandaloneBatch / Connections=1&count=32 | -0.58%; -0.66% | +0.08% | 259.346598–261.375840 / 257.746460–259.975076 / 259.823004–261.319470 | 11253 / 11252 / 11253 |
| StandaloneBatch / Connections=2&count=100 | -0.46%; -0.04% | -0.42% | 358.622846–364.847628 / 358.472918–361.664257 / 357.895665–362.536613 | 32411 / 32411 / 32408 |
| StandaloneBatch / Connections=2&count=32 | +0.39%; -0.62% | +1.01% | 256.776492–258.746867 / 256.905797–260.610693 / 259.024851–261.716214 | 11253 / 11253 / 11252 |
| StreamedSetWithDelayedSource / Connections=1 | -3.77%; -4.09% | +0.33% | 10004.800625–10127.925036 / 9384.274745–9988.464538 / 10041.417844–10158.336278 | 12402 / 12360 / 12462 |
| StreamedSetWithDelayedSource / Connections=2 | -4.02%; -4.92% | +0.94% | 10060.977444–10178.790267 / 9389.571518–10036.325563 / 10135.584073–10294.645105 | 12468 / 12488 / 12552 |
| StreamedSetWithInstantSource / Connections=1 | +0.41%; +0.05% | +0.36% | 364.335836–372.010545 / 366.252780–373.089015 / 365.142915–373.840062 | 6486 / 6518 / 6504 |
| StreamedSetWithInstantSource / Connections=2 | +0.27%; -0.07% | +0.34% | 361.582237–371.013315 / 362.933020–371.635876 / 362.560277–372.552659 | 6533 / 6454 / 6485 |

| Case / parameters | PID 1, 2: A / candidate / B | Launch 1, 2 mean us: A / candidate / B | Raw launch 1, 2 B/op: A / candidate / B |
| --- | --- | --- | --- |
| CircuitStreamedGet / Connections=1 | 9657, 9749 / 15546, 15631 / 21273, 21344 | 393.520545, 387.547973 / 389.741105, 388.436993 / 393.770655, 395.883352 | 17782.641, 19011.961 / 16859.273, 18777.953 / 17366.781, 17834.250 |
| CircuitStreamedGet / Connections=2 | 11965, 12053 / 17877, 17956 / 23637, 23721 | 380.396602, 380.543414 / 387.245129, 386.359092 / 386.837550, 377.735351 | 18519.273, 20940.523 / 19872.102, 19691.188 / 18896.008, 18878.117 |
| ClientPing / Connections=1 | 9881, 10009 / 15700, 15822 / 21428, 21535 | 192.190337, 190.671251 / 192.313961, 189.507436 / 192.939426, 193.458506 | 183.979, 184.104 / 183.754, 184.068 / 184.135, 183.848 |
| ClientPing / Connections=2 | 12124, 12249 / 18045, 18146 / 23789, 23922 | 191.792572, 191.332403 / 194.001206, 193.253886 / 193.799891, 194.144731 | 184.219, 184.225 / 184.225, 183.666 / 184.225, 183.334 |
| ClientServerError / Connections=1 | 10120, 10201 / 15927, 16049 / 21658, 21779 | 222.710870, 223.063194 / 222.251760, 222.824067 / 225.762693, 226.141451 | 952.293, 952.355 / 952.193, 951.928 / 952.178, 952.193 |
| ClientServerError / Connections=2 | 12364, 12483 / 18271, 18328 / 24040, 24098 | 222.900901, 222.639788 / 225.327924, 224.669947 / 226.950652, 225.142475 | 952.283, 952.535 / 952.207, 952.207 / 952.117, 952.086 |
| LargeSet / Connections=1 | 9150, 9228 / 14928, 15054 / 20747, 20809 | 1768.912764, 1827.292964 / 1832.286346, 1823.895367 / 1825.961559, 1823.169293 | 178.156, 176.000 / 177.078, 176.000 / 176.000, 176.000 |
| LargeSet / Connections=2 | 11438, 11521 / 17350, 17426 / 23073, 23152 | 1787.453641, 1784.342761 / 1809.608320, 1808.609514 / 1834.752781, 1870.070729 | 176.719, 176.000 / 176.000, 176.000 / 176.000, 179.594 |
| Pipeline / Connections=1 | 8546, 8625 / 14322, 14469 / 20096, 20176 | 6.709118, 6.772015 / 6.712565, 6.712363 / 6.788397, 6.767577 | 6.758, 6.763 / 6.763, 6.765 / 6.770, 6.765 |
| Pipeline / Connections=2 | 10887, 10967 / 16770, 16851 / 22505, 22587 | 7.662673, 7.724783 / 7.689510, 7.820283 / 7.819508, 7.851635 | 6.770, 6.757 / 6.778, 6.767 / 6.764, 6.768 |
| PublicPipeline / Connections=1 | 8756, 8819 / 14534, 14615 / 20299, 20380 | 6.751880, 6.767432 / 6.794841, 6.785658 / 6.798362, 6.790585 | 5.764, 5.769 / 5.772, 5.771 / 5.762, 5.770 |
| PublicPipeline / Connections=2 | 11048, 11131 / 16932, 17017 / 22687, 22767 | 7.865078, 7.910098 / 7.884166, 7.754777 / 7.958910, 7.909327 | 5.776, 5.787 / 5.777, 5.762 / 5.776, 5.776 |
| SelectConnection / Connections=1 | 8213, 8366 / 14010, 14176 / 19782, 19932 | 0.002648, 0.002674 / 0.002635, 0.002641 / 0.002648, 0.002642 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 10636, 10763 / 16546, 16647 / 22270, 22403 | 0.007025, 0.006388 / 0.017567, 0.006419 / 0.006467, 0.006388 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 8904, 9042 / 14694, 14799 / 20502, 20621 | 195.527933, 196.303077 / 195.442730, 195.415058 / 196.156029, 197.096523 | 176.000, 176.074 / 176.045, 175.982 / 176.000, 174.918 |
| SmallSet / Connections=2 | 11212, 11334 / 17099, 17224 / 22851, 22953 | 198.221210, 195.404174 / 196.685177, 196.414838 / 199.537697, 199.157851 | 176.209, 175.934 / 176.008, 176.180 / 176.100, 176.209 |
| StandaloneBatch / Connections=1&count=100 | 8025, 8131 / 13774, 13898 / 19642, 19706 | 361.263545, 359.327201 / 366.034874, 363.200223 / 360.303220, 361.043056 | 32406.898, 32407.219 / 32413.629, 32410.219 / 32406.594, 32412.711 |
| StandaloneBatch / Connections=1&count=32 | 7855, 7943 / 13610, 13693 / 19480, 19564 | 260.939750, 259.741365 / 258.299560, 259.384562 / 260.354568, 260.787906 | 11252.449, 11253.137 / 11252.539, 11251.699 / 11252.199, 11253.449 |
| StandaloneBatch / Connections=2&count=100 | 10447, 10549 / 16359, 16464 / 22083, 22187 | 359.660360, 363.533464 / 360.527741, 359.609434 / 358.444949, 362.113843 | 32408.777, 32411.438 / 32415.527, 32411.117 / 32407.098, 32407.938 |
| StandaloneBatch / Connections=2&count=32 | 10282, 10364 / 16193, 16276 / 21902, 21984 | 258.618514, 256.904845 / 259.963937, 257.632932 / 259.239463, 261.501602 | 11250.398, 11252.988 / 11248.738, 11252.988 / 11251.398, 11252.438 |
| StreamedSetWithDelayedSource / Connections=1 | 9306, 9432 / 15135, 15214 / 20888, 20989 | 10096.605647, 10036.120014 / 9264.010239, 10108.729044 / 10088.892345, 10112.551733 | 12586.250, 12401.625 / 12365.500, 12360.125 / 12557.250, 12461.625 |
| StreamedSetWithDelayedSource / Connections=2 | 11601, 11699 / 17490, 17591 / 23234, 23333 | 10141.362999, 10098.404712 / 9249.931589, 10175.965492 / 10262.882569, 10167.346609 | 12375.000, 12467.875 / 12424.875, 12488.500 / 12423.125, 12551.500 |
| StreamedSetWithInstantSource / Connections=1 | 9515, 9597 / 15314, 15435 / 21088, 21192 | 368.163088, 368.184014 / 369.642107, 369.697769 / 368.824572, 370.158404 | 6424.867, 6485.703 / 6459.496, 6518.387 / 6502.977, 6504.398 |
| StreamedSetWithInstantSource / Connections=2 | 11779, 11903 / 17675, 17798 / 23434, 23533 | 365.730394, 366.952446 / 367.177971, 367.390926 / 366.898667, 368.170416 | 6483.250, 6533.484 / 6233.605, 6453.531 / 6480.914, 6485.492 |

| Phase | Connections | Stage | Samples | Managed bytes | POH bytes | POH fragmentation | Working set bytes | Private bytes |
| --- | ---: | --- | ---: | --- | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 24 | 5913264–5927632 | 8184–8184 | 0–0 | 49553408–49860608 | 134438912–134479872 |
| baseline-a | 1 | connected | 24 | 8680680–8697176 | 24528–24528 | 0–0 | 67948544–68386816 | 261181440–261410816 |
| baseline-a | 1 | after-churn | 24 | 9704288–9724752 | 24528–24528 | 0–0 | 71360512–71868416 | 263585792–280649728 |
| baseline-a | 1 | after-dispose | 24 | 8243144–8862120 | 24528–24528 | 0–0 | 75034624–102412288 | 246202368–456126464 |
| baseline-a | 2 | before-connect | 24 | 5913144–5927616 | 8184–8184 | 0–0 | 49541120–49942528 | 134451200–134676480 |
| baseline-a | 2 | connected | 24 | 10289544–10306184 | 24528–24528 | 0–0 | 69787648–70205440 | 263671808–263872512 |
| baseline-a | 2 | after-churn | 24 | 11319408–11350008 | 24528–24528 | 0–0 | 74076160–74539008 | 283693056–283942912 |
| baseline-a | 2 | after-dispose | 24 | 8970768–10546608 | 24528–24528 | 0–0 | 75116544–103141376 | 262860800–473804800 |
| candidate | 1 | before-connect | 24 | 5913232–5927640 | 8184–8184 | 0–0 | 49467392–49876992 | 134438912–134676480 |
| candidate | 1 | connected | 24 | 8680696–8701304 | 24528–24528 | 0–0 | 67936256–68378624 | 261148672–261451776 |
| candidate | 1 | after-churn | 24 | 9706976–9715704 | 24528–24528 | 0–0 | 71450624–71794688 | 263634944–280625152 |
| candidate | 1 | after-dispose | 24 | 8245640–9042112 | 24528–24528 | 0–0 | 74928128–106610688 | 246280192–406065152 |
| candidate | 2 | before-connect | 24 | 5913248–5927720 | 8184–8184 | 0–0 | 49586176–49897472 | 134434816–134479872 |
| candidate | 2 | connected | 24 | 10289648–10306224 | 24528–24528 | 0–0 | 69869568–70369280 | 263602176–263876608 |
| candidate | 2 | after-churn | 24 | 11262232–11339784 | 24528–24528 | 0–0 | 74145792–74584064 | 283697152–283942912 |
| candidate | 2 | after-dispose | 24 | 8963760–10039688 | 24528–24528 | 0–0 | 75456512–102637568 | 263241728–474107904 |
| baseline-b | 1 | before-connect | 24 | 5919264–5927632 | 8184–8184 | 0–0 | 49520640–49975296 | 134434816–134676480 |
| baseline-b | 1 | connected | 24 | 8680584–8697216 | 24528–24528 | 0–0 | 67948544–68407296 | 261201920–261439488 |
| baseline-b | 1 | after-churn | 24 | 9702744–9718168 | 24528–24528 | 0–0 | 71385088–71974912 | 263610368–280686592 |
| baseline-b | 1 | after-dispose | 24 | 8243384–9000776 | 24528–24528 | 0–0 | 74956800–105897984 | 246210560–410271744 |
| baseline-b | 2 | before-connect | 24 | 5913128–5927616 | 8184–8184 | 0–0 | 49557504–49840128 | 134438912–134479872 |
| baseline-b | 2 | connected | 24 | 10298792–10314304 | 24528–24528 | 0–0 | 69861376–70209536 | 263655424–263864320 |
| baseline-b | 2 | after-churn | 24 | 11320120–11336728 | 24528–24528 | 0–0 | 74141696–74444800 | 283693056–283947008 |
| baseline-b | 2 | after-dispose | 24 | 9022120–9967800 | 24528–24528 | 0–0 | 75005952–103690240 | 262770688–476291072 |

</details>

### Run 38193859429: error metrics enabled

Baseline `e28016d4041c5ab417f6b6f6565c63a46ed8af56`; measured candidate `815eec226f2e97fe7f8b28591950e73c4b881da2`; event head `9c04cc60fb3ca438cd833d279495e6733e210239`.

<details>
<summary>Every public interval, allocation row, launch and retention group</summary>

| Case / parameters | Candidate/A; candidate/B | B/A drift | 99.9% interval us: A / candidate / B | Reported B/op: A / candidate / B |
| --- | ---: | ---: | --- | --- |
| CircuitStreamedGet / Connections=1 | -0.05%; +1.46% | -1.48% | 285.532567–292.027493 / 284.319982–292.977973 / 282.055064–286.942241 | 21979 / 22780 / 22706 |
| CircuitStreamedGet / Connections=2 | +0.58%; -0.19% | +0.78% | 273.480324–278.433330 / 274.673875–280.455231 / 275.135537–281.071322 | 23998 / 21373 / 20502 |
| ClientPing / Connections=1 | -0.77%; -1.30% | +0.53% | 90.438841–94.999575 / 89.541219–94.469474 / 90.724898–95.704273 | 183 / 183 / 183 |
| ClientPing / Connections=2 | -1.11%; +1.61% | -2.68% | 94.496184–97.770377 / 92.515226–97.608625 / 90.991043–96.128582 | 184 / 182 / 183 |
| ClientServerError / Connections=1 | +1.42%; +1.30% | +0.12% | 129.661646–131.458195 / 131.686999–133.145994 / 129.956615–131.480545 | 952 / 952 / 952 |
| ClientServerError / Connections=2 | +0.82%; +1.06% | -0.23% | 131.010255–132.625168 / 132.124841–133.685238 / 130.564030–132.465787 | 952 / 952 / 952 |
| LargeSet / Connections=1 | +3.01%; -0.45% | +3.48% | 1227.880601–1265.751157 / 1256.159938–1312.570271 / 1262.013493–1318.320563 | 177 / 176 / 176 |
| LargeSet / Connections=2 | +6.36%; +4.72% | +1.57% | 1192.073042–1227.770136 / 1260.886603–1312.941830 / 1206.017500–1251.862206 | 177 / 176 / 177 |
| Pipeline / Connections=1 | -0.22%; +0.08% | -0.30% | 4.010606–4.044883 / 4.003622–4.034022 / 4.001236–4.030097 | 7 / 7 / 7 |
| Pipeline / Connections=2 | -0.57%; -0.27% | -0.31% | 4.658201–4.792989 / 4.637501–4.759553 / 4.669895–4.752441 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | +0.02%; -0.03% | +0.04% | 4.007703–4.042302 / 4.010859–4.040409 / 4.002035–4.051381 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | -0.40%; +0.74% | -1.14% | 4.694293–4.874547 / 4.705919–4.824461 / 4.667118–4.792994 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | -9.83%; -4.87% | -5.22% | 0.001632–0.001672 / 0.001478–0.001501 / 0.001537–0.001594 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | -0.30%; -7.27% | +7.52% | 0.004759–0.004780 / 0.004748–0.004762 / 0.004898–0.005358 | 0 / 0 / 0 |
| SmallSet / Connections=1 | -1.30%; +0.04% | -1.34% | 105.667541–107.157237 / 104.228303–105.836469 / 102.771486–107.200778 | 176 / 174 / 175 |
| SmallSet / Connections=2 | -2.16%; -1.95% | -0.21% | 105.955753–108.009862 / 103.439137–105.913479 / 105.313480–108.201071 | 176 / 176 / 176 |
| StandaloneBatch / Connections=1&count=100 | -0.12%; -0.21% | +0.09% | 243.364713–245.923203 / 243.341850–245.341946 / 243.986176–245.749286 | 32407 / 32391 / 32402 |
| StandaloneBatch / Connections=1&count=32 | +0.26%; +0.04% | +0.22% | 159.150272–159.927687 / 159.460715–160.462673 / 159.510689–160.270554 | 11242 / 11247 / 11247 |
| StandaloneBatch / Connections=2&count=100 | +0.22%; -0.13% | +0.35% | 240.925626–243.618397 / 241.671366–243.956499 / 241.585704–244.676776 | 32394 / 32400 / 32390 |
| StandaloneBatch / Connections=2&count=32 | +0.48%; +0.67% | -0.18% | 158.951346–160.348795 / 159.773482–161.075235 / 158.954247–159.765950 | 11248 / 11250 / 11248 |
| StreamedSetWithDelayedSource / Connections=1 | +1.02%; +0.70% | +0.32% | 9343.853501–9513.078911 / 9458.732819–9591.479644 / 9395.628979–9522.226057 | 12522 / 12366 / 12396 |
| StreamedSetWithDelayedSource / Connections=2 | -0.16%; -0.76% | +0.60% | 9017.286748–9445.842578 / 9073.487123–9359.262609 / 9118.386959–9456.114386 | 12270 / 12578 / 12360 |
| StreamedSetWithInstantSource / Connections=1 | -4.17%; -0.08% | -4.09% | 250.638238–261.662047 / 242.118657–248.797121 / 241.097814–250.227649 | 6472 / 6487 / 6464 |
| StreamedSetWithInstantSource / Connections=2 | +2.30%; +1.44% | +0.85% | 240.929321–247.292544 / 246.372534–253.090397 / 242.514328–249.878215 | 6521 / 6526 / 6487 |

| Case / parameters | PID 1, 2: A / candidate / B | Launch 1, 2 mean us: A / candidate / B | Raw launch 1, 2 B/op: A / candidate / B |
| --- | --- | --- | --- |
| CircuitStreamedGet / Connections=1 | 8806, 8916 / 14410, 14515 / 19980, 20082 | 288.683702, 288.876358 / 289.573850, 287.724106 / 286.052778, 283.055536 | 21641.234, 21978.750 / 20569.469, 22780.094 / 22245.023, 22705.781 |
| CircuitStreamedGet / Connections=2 | 11139, 11238 / 16688, 16777 / 22319, 22402 | 274.160187, 277.633691 / 277.828289, 277.260242 / 277.924703, 278.309652 | 21478.031, 23997.820 / 21711.309, 21373.055 / 20494.316, 20501.594 |
| ClientPing / Connections=1 | 9001, 9123 / 14606, 14730 / 20235, 20354 | 91.878295, 93.560122 / 93.787409, 90.223285 / 91.611106, 94.818064 | 182.743, 182.984 / 180.044, 182.510 / 181.772, 183.278 |
| ClientPing / Connections=2 | 11323, 11450 / 16884, 16990 / 22512, 22611 | 96.224280, 96.048348 / 94.366932, 95.756919 / 91.197921, 95.921704 | 182.315, 183.688 / 183.804, 182.021 / 182.731, 183.358 |
| ClientServerError / Connections=1 | 9228, 9312 / 14831, 14911 / 20456, 20543 | 129.798065, 131.376194 / 132.976857, 131.856136 / 131.413622, 129.916609 | 952.193, 952.088 / 951.934, 952.059 / 952.104, 952.162 |
| ClientServerError / Connections=2 | 11550, 11639 / 17117, 17200 / 22741, 22821 | 132.649514, 131.041363 / 132.164098, 133.698906 / 132.594540, 130.507253 | 952.043, 951.926 / 952.059, 952.043 / 952.059, 952.104 |
| LargeSet / Connections=1 | 8261, 8342 / 13875, 13959 / 19459, 19564 | 1248.802152, 1244.829606 / 1267.743250, 1300.986959 / 1309.102825, 1271.231230 | 177.078, 177.078 / 177.078, 176.000 / 177.078, 176.359 |
| LargeSet / Connections=2 | 10611, 10712 / 16152, 16248 / 21800, 21879 | 1205.902873, 1214.227355 / 1286.324935, 1287.464213 / 1244.051345, 1212.748969 | 176.000, 177.438 / 176.000, 175.875 / 177.797, 177.438 |
| Pipeline / Connections=1 | 7657, 7739 / 13279, 13360 / 18860, 18943 | 4.017865, 4.036919 / 4.025910, 4.011734 / 4.014072, 4.017262 | 6.759, 6.759 / 6.752, 6.753 / 6.760, 6.715 |
| Pipeline / Connections=2 | 10006, 10104 / 15563, 15671 / 21172, 21271 | 4.755184, 4.696007 / 4.690850, 4.706204 / 4.705974, 4.716015 | 6.727, 6.706 / 6.703, 6.724 / 6.736, 6.738 |
| PublicPipeline / Connections=1 | 7840, 7922 / 13459, 13540 / 19045, 19124 | 4.024026, 4.026048 / 4.023781, 4.027355 / 4.023086, 4.030072 | 5.743, 5.747 / 5.762, 5.752 / 5.759, 5.756 |
| PublicPipeline / Connections=2 | 10213, 10300 / 15765, 15845 / 21356, 21454 | 4.808228, 4.760612 / 4.736626, 4.795794 / 4.733191, 4.727145 | 5.732, 5.731 / 5.733, 5.675 / 5.726, 5.698 |
| SelectConnection / Connections=1 | 7394, 7529 / 13012, 13153 / 18588, 18731 | 0.001634, 0.001672 / 0.001481, 0.001498 / 0.001557, 0.001574 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 9803, 9906 / 15355, 15457 / 20964, 21069 | 0.004760, 0.004778 / 0.004757, 0.004753 / 0.005413, 0.004796 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 8003, 8133 / 13625, 13753 / 19206, 19332 | 106.909935, 105.879304 / 105.418924, 104.618238 / 103.056837, 106.915427 | 175.192, 175.606 / 175.685, 173.730 / 175.450, 174.866 |
| SmallSet / Connections=2 | 10360, 10487 / 15904, 16026 / 21554, 21657 | 107.324084, 106.641531 / 104.303911, 105.048705 / 105.005953, 108.508598 | 175.926, 175.843 / 175.495, 175.987 / 175.621, 175.878 |
| StandaloneBatch / Connections=1&count=100 | 7248, 7312 / 12849, 12929 / 18423, 18504 | 243.670197, 245.617718 / 245.555916, 243.289749 / 245.471311, 244.264151 | 32391.719, 32406.668 / 32389.348, 32390.539 / 32374.438, 32402.449 |
| StandaloneBatch / Connections=1&count=32 | 7050, 7141 / 12660, 12769 / 18239, 18322 | 159.714045, 159.363914 / 160.176524, 159.762210 / 159.754734, 160.008390 | 11249.998, 11242.143 / 11248.730, 11247.498 / 11249.199, 11246.824 |
| StandaloneBatch / Connections=2&count=100 | 9639, 9722 / 15199, 15276 / 20826, 20905 | 243.068476, 241.528645 / 242.267826, 243.360039 / 241.385985, 244.760145 | 32404.078, 32394.148 / 32394.258, 32399.898 / 32412.148, 32389.977 |
| StandaloneBatch / Connections=2&count=32 | 9393, 9455 / 14994, 15096 / 20622, 20724 | 159.130498, 160.169643 / 159.977189, 160.841716 / 159.041960, 159.678237 | 11251.273, 11247.842 / 11248.748, 11249.623 / 11248.076, 11248.250 |
| StreamedSetWithDelayedSource / Connections=1 | 8443, 8545 / 14058, 14157 / 19642, 19740 | 9450.804595, 9406.127817 / 9531.468844, 9518.289147 / 9477.756279, 9438.753846 | 12458.375, 12522.500 / 12237.875, 12366.250 / 12489.500, 12396.500 |
| StreamedSetWithDelayedSource / Connections=2 | 10814, 10893 / 16344, 16422 / 21975, 22055 | 9472.603030, 8953.443470 / 9384.112735, 9048.636997 / 9335.115648, 9239.385697 | 12810.750, 12270.125 / 12278.875, 12578.500 / 12527.625, 12360.125 |
| StreamedSetWithInstantSource / Connections=1 | 8631, 8721 / 14237, 14325 / 19821, 19900 | 262.138902, 250.161383 / 247.335606, 243.714295 / 247.436190, 243.889272 | 6529.000, 6471.562 / 6535.730, 6487.199 / 6521.059, 6464.492 |
| StreamedSetWithInstantSource / Connections=2 | 10973, 11058 / 16520, 16603 / 22153, 22237 | 246.044017, 241.880450 / 249.440284, 250.003235 / 247.225264, 245.008972 | 6528.500, 6520.531 / 6446.340, 6525.762 / 6465.328, 6486.547 |

| Phase | Connections | Stage | Samples | Managed bytes | POH bytes | POH fragmentation | Working set bytes | Private bytes |
| --- | ---: | --- | ---: | --- | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 24 | 5913432–5921784 | 8184–8184 | 0–0 | 49709056–49954816 | 134438912–134483968 |
| baseline-a | 1 | connected | 24 | 8681288–8693768 | 24528–24528 | 0–0 | 68038656–68460544 | 261185536–261406720 |
| baseline-a | 1 | after-churn | 24 | 9701552–9719256 | 24528–24528 | 0–0 | 71483392–72015872 | 263442432–280608768 |
| baseline-a | 1 | after-dispose | 24 | 8365680–9086592 | 24528–24528 | 0–0 | 75448320–102653952 | 263069696–406573056 |
| baseline-a | 2 | before-connect | 24 | 5913424–5927800 | 8184–8184 | 0–0 | 49598464–49995776 | 134443008–134676480 |
| baseline-a | 2 | connected | 24 | 10287176–10310808 | 24528–24528 | 0–0 | 69910528–70230016 | 263667712–280674304 |
| baseline-a | 2 | after-churn | 24 | 11321032–11347896 | 24528–24528 | 0–0 | 74133504–74571776 | 266944512–283893760 |
| baseline-a | 2 | after-dispose | 24 | 8972760–9809936 | 24528–24528 | 0–0 | 75710464–102707200 | 280027136–388866048 |
| candidate | 1 | before-connect | 24 | 5913544–5921896 | 8184–8184 | 0–0 | 49676288–50012160 | 134443008–134676480 |
| candidate | 1 | connected | 24 | 8681416–8698016 | 24528–24528 | 0–0 | 68046848–68407296 | 261210112–261378048 |
| candidate | 1 | after-churn | 24 | 9702112–9721200 | 24528–24528 | 0–0 | 71483392–71917568 | 263479296–280563712 |
| candidate | 1 | after-dispose | 24 | 8304248–9057472 | 24528–24528 | 0–0 | 75223040–102809600 | 263118848–389296128 |
| candidate | 2 | before-connect | 24 | 5913536–5921904 | 8184–8184 | 0–0 | 49635328–50008064 | 134443008–134676480 |
| candidate | 2 | connected | 24 | 10290400–10307936 | 24528–24528 | 0–0 | 69890048–70262784 | 263614464–280768512 |
| candidate | 2 | after-churn | 24 | 11321504–11345120 | 24528–24528 | 0–0 | 74215424–74592256 | 266899456–283971584 |
| candidate | 2 | after-dispose | 24 | 8997296–10112248 | 24528–24528 | 0–0 | 75276288–102965248 | 262885376–473894912 |
| baseline-b | 1 | before-connect | 24 | 5913536–5927912 | 8184–8184 | 0–0 | 49532928–50053120 | 134443008–134479872 |
| baseline-b | 1 | connected | 24 | 8681472–8697912 | 24528–24528 | 0–0 | 68005888–68501504 | 261193728–261361664 |
| baseline-b | 1 | after-churn | 24 | 9699392–9721824 | 24528–24528 | 0–0 | 71544832–71979008 | 263528448–280555520 |
| baseline-b | 1 | after-dispose | 24 | 8306728–9005584 | 24528–24528 | 0–0 | 75329536–103444480 | 263049216–490668032 |
| baseline-b | 2 | before-connect | 24 | 5913424–5927808 | 8184–8184 | 0–0 | 49590272–50020352 | 134438912–134483968 |
| baseline-b | 2 | connected | 24 | 10290376–10314208 | 24528–24528 | 0–0 | 69906432–70352896 | 263602176–263856128 |
| baseline-b | 2 | after-churn | 24 | 11317888–11343768 | 24528–24528 | 0–0 | 74166272–74645504 | 266764288–283930624 |
| baseline-b | 2 | after-dispose | 24 | 9063032–10109512 | 24528–24528 | 0–0 | 74547200–102891520 | 280227840–406056960 |

</details>

### Run 38195553203: error metrics disabled

Baseline `dfa7564f3946695f00c48f8d71679fdce935c8c2`; measured candidate `fbc7af5a11404398455df510a2ddb93415d05c05`; event head `ba10a10c7132e891d1bd02431a97ba31cbd9d3e5`.

<details>
<summary>Every public interval, allocation row, launch and retention group</summary>

| Case / parameters | Candidate/A; candidate/B | B/A drift | 99.9% interval us: A / candidate / B | Reported B/op: A / candidate / B |
| --- | ---: | ---: | --- | --- |
| CircuitStreamedGet / Connections=1 | -1.26%; -3.32% | +2.12% | 192.186861–202.197356 / 190.118201–199.287099 / 198.442792–204.313973 | 24205 / 23873 / 21299 |
| CircuitStreamedGet / Connections=2 | -0.14%; -3.02% | +2.97% | 195.822544–201.345818 / 194.870354–201.756025 / 201.654417–207.316961 | 22607 / 22035 / 23022 |
| ClientPing / Connections=1 | +1.91%; -2.61% | +4.64% | 68.630647–71.916776 / 71.022348–72.209725 / 72.014956–75.052089 | 183 / 184 / 183 |
| ClientPing / Connections=2 | -0.22%; -1.46% | +1.26% | 73.494355–75.269965 / 73.532570–74.907173 / 73.986352–76.651236 | 179 / 184 / 183 |
| ClientServerError / Connections=1 | -0.13%; -1.17% | +1.05% | 80.561426–83.659803 / 80.539901–83.471271 / 81.190365–84.759284 | 949 / 951 / 952 |
| ClientServerError / Connections=2 | +0.88%; -4.45% | +5.59% | 81.541826–84.704410 / 82.556568–85.160812 / 85.173205–90.362662 | 952 / 948 / 950 |
| LargeSet / Connections=1 | -2.03%; -3.01% | +1.01% | 954.928564–993.526615 / 943.278101–965.697966 / 968.606307–999.613791 | 177 / 177 / 176 |
| LargeSet / Connections=2 | -4.52%; -2.61% | -1.95% | 957.589935–999.126006 / 920.078084–948.284319 / 951.520922–966.954247 | 176 / 177 / 178 |
| Pipeline / Connections=1 | -0.79%; -1.20% | +0.41% | 3.149487–3.244416 / 3.139910–3.203321 / 3.176715–3.243571 | 7 / 7 / 7 |
| Pipeline / Connections=2 | -0.87%; -4.31% | +3.60% | 3.666087–3.778908 / 3.631674–3.748365 / 3.794893–3.917873 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | -0.54%; -0.70% | +0.16% | 3.188448–3.266032 / 3.188036–3.231526 / 3.182972–3.281593 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | -1.24%; -3.39% | +2.23% | 3.695387–3.879572 / 3.680581–3.800564 / 3.814087–3.929779 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | +14.11%; +13.79% | +0.28% | 0.001000–0.001032 / 0.001047–0.001272 / 0.000994–0.001044 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | +0.74%; -1.89% | +2.68% | 0.006872–0.006928 / 0.006840–0.007061 / 0.007012–0.007158 | 0 / 0 / 0 |
| SmallSet / Connections=1 | -1.45%; -2.11% | +0.67% | 73.170657–75.494615 / 71.985066–74.519609 / 73.809765–75.856966 | 175 / 174 / 173 |
| SmallSet / Connections=2 | -3.54%; -4.54% | +1.04% | 72.908923–75.678200 / 70.803050–72.519349 / 73.786321–76.347709 | 175 / 176 / 173 |
| StandaloneBatch / Connections=1&count=100 | +1.50%; -2.08% | +3.65% | 169.950064–174.989782 / 173.673064–176.429713 / 177.387773–180.155702 | 32410 / 32416 / 32414 |
| StandaloneBatch / Connections=1&count=32 | +3.97%; +1.57% | +2.36% | 116.825095–120.215701 / 121.835622–124.606774 / 120.330989–122.296607 | 11252 / 11250 / 11250 |
| StandaloneBatch / Connections=2&count=100 | -1.12%; -4.54% | +3.58% | 170.960306–173.243756 / 169.113246–171.232759 / 175.799213–180.721828 | 32417 / 32416 / 32416 |
| StandaloneBatch / Connections=2&count=32 | +0.12%; -3.51% | +3.76% | 118.771759–121.350938 / 119.242810–121.171467 / 123.530653–125.617652 | 11241 / 11251 / 11249 |
| StreamedSetWithDelayedSource / Connections=1 | +0.40%; -0.01% | +0.40% | 9155.447951–9372.588517 / 9208.658227–9393.265533 / 9215.936398–9387.103704 | 12335 / 12608 / 12541 |
| StreamedSetWithDelayedSource / Connections=2 | -1.49%; -0.60% | -0.89% | 9106.616712–9455.829733 / 9001.637527–9283.987014 / 9041.582847–9355.242206 | 12326 / 12424 / 12451 |
| StreamedSetWithInstantSource / Connections=1 | -0.81%; -0.22% | -0.59% | 175.431915–182.561406 / 175.540006–179.558918 / 175.923040–179.963853 | 6358 / 6119 / 6342 |
| StreamedSetWithInstantSource / Connections=2 | -4.37%; -4.42% | +0.05% | 178.882552–184.426937 / 172.622216–174.820680 / 179.472343–184.028643 | 6511 / 6415 / 6094 |

| Case / parameters | PID 1, 2: A / candidate / B | Launch 1, 2 mean us: A / candidate / B | Raw launch 1, 2 B/op: A / candidate / B |
| --- | --- | --- | --- |
| CircuitStreamedGet / Connections=1 | 8720, 8789 / 14336, 14425 / 20089, 20192 | 197.203101, 197.181115 / 198.838394, 190.842622 / 201.950643, 200.718081 | 22420.227, 24205.193 / 22787.418, 23873.254 / 23119.459, 21299.064 |
| CircuitStreamedGet / Connections=2 | 11128, 11194 / 16719, 16801 / 22496, 22623 | 200.176515, 197.114334 / 195.392570, 201.233810 / 204.309039, 204.674957 | 21780.016, 22607.211 / 23807.480, 22035.135 / 23772.846, 23021.727 |
| ClientPing / Connections=1 | 8914, 8994 / 14487, 14590 / 20316, 20416 | 72.234593, 68.574281 / 71.773390, 71.458683 / 74.656012, 72.411034 | 183.719, 182.622 / 183.500, 183.742 / 183.351, 183.315 |
| ClientPing / Connections=2 | 11278, 11366 / 16906, 17015 / 22701, 22798 | 74.898986, 73.828417 / 74.341777, 74.114220 / 74.822216, 75.850842 | 183.190, 178.554 / 183.440, 183.620 / 182.849, 183.272 |
| ClientServerError / Connections=1 | 9097, 9177 / 14666, 14762 / 20496, 20594 | 83.104385, 81.045860 / 81.526792, 82.452461 / 83.223033, 82.726615 | 951.776, 949.273 / 951.339, 951.409 / 950.963, 951.974 |
| ClientServerError / Connections=2 | 11472, 11567 / 17099, 17202 / 22896, 22998 | 82.170279, 84.075958 / 84.146360, 83.571021 / 88.622955, 86.912911 | 952.098, 951.779 / 952.029, 948.058 / 951.720, 950.126 |
| LargeSet / Connections=1 | 8088, 8236 / 13810, 13869 / 19528, 19605 | 993.953005, 953.093216 / 955.643731, 953.332336 / 978.754977, 989.847627 | 176.539, 177.078 / 176.719, 177.078 / 177.078, 176.359 |
| LargeSet / Connections=2 | 10568, 10645 / 16128, 16210 / 21879, 22023 | 965.128314, 991.587626 / 920.174433, 950.342857 / 961.353253, 957.262960 | 177.797, 176.000 / 176.359, 176.898 / 176.898, 178.047 |
| Pipeline / Connections=1 | 7552, 7634 / 13338, 13400 / 19020, 19083 | 3.175902, 3.218001 / 3.146222, 3.197009 / 3.221151, 3.200603 | 6.755, 6.754 / 6.745, 6.749 / 6.754, 6.746 |
| Pipeline / Connections=2 | 9998, 10078 / 15578, 15705 / 21305, 21459 | 3.675621, 3.763125 / 3.674633, 3.705406 / 3.825828, 3.886938 | 6.646, 6.611 / 6.580, 6.671 / 6.606, 6.671 |
| PublicPipeline / Connections=1 | 7763, 7825 / 13481, 13540 / 19164, 19240 | 3.247852, 3.208002 / 3.208512, 3.211051 / 3.285437, 3.182672 | 5.750, 5.756 / 5.755, 5.748 / 5.746, 5.708 |
| PublicPipeline / Connections=2 | 10160, 10246 / 15789, 15870 / 21546, 21626 | 3.721689, 3.848884 / 3.776203, 3.704942 / 3.851184, 3.892683 | 5.608, 5.593 / 5.685, 5.627 / 5.692, 5.571 |
| SelectConnection / Connections=1 | 7170, 7371 / 12927, 13126 / 18643, 18838 | 0.001014, 0.001019 / 0.001001, 0.001296 / 0.001024, 0.001014 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 9786, 9891 / 15348, 15453 / 21100, 21203 | 0.006911, 0.006889 / 0.006834, 0.007068 / 0.007093, 0.007077 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 7909, 7988 / 13622, 13707 / 19348, 19449 | 73.592815, 75.195760 / 73.614301, 72.862530 / 75.127392, 74.539339 | 175.567, 175.351 / 175.643, 174.096 / 168.262, 173.290 |
| SmallSet / Connections=2 | 10331, 10489 / 15948, 16026 / 21706, 21806 | 73.803268, 74.751168 / 71.497179, 71.814285 / 75.506265, 74.627765 | 167.190, 175.494 / 172.312, 175.703 / 173.239, 172.792 |
| StandaloneBatch / Connections=1&count=100 | 6921, 7028 / 12714, 12821 / 18413, 18538 | 171.620179, 173.450396 / 176.356428, 173.653132 / 177.689318, 179.854157 | 32406.840, 32409.668 / 32414.672, 32415.951 / 32413.385, 32413.824 |
| StandaloneBatch / Connections=1&count=32 | 6671, 6757 / 12553, 12636 / 18196, 18333 | 120.241803, 116.676035 / 121.989793, 124.370510 / 121.262260, 121.365336 | 11250.783, 11251.604 / 11249.600, 11250.053 / 11248.815, 11249.520 |
| StandaloneBatch / Connections=2&count=100 | 9578, 9683 / 15134, 15241 / 20851, 20915 | 171.579224, 172.555130 / 169.106447, 171.239558 / 177.988609, 178.532431 | 32417.760, 32417.494 / 32417.465, 32415.838 / 32412.488, 32416.057 |
| StandaloneBatch / Connections=2&count=32 | 9281, 9433 / 14864, 14991 / 20691, 20773 | 121.023058, 119.030945 / 120.512956, 119.879477 / 125.564977, 123.512555 | 11249.827, 11240.968 / 11251.366, 11251.187 / 11247.559, 11249.012 |
| StreamedSetWithDelayedSource / Connections=1 | 8294, 8393 / 13951, 14047 / 19683, 19784 | 9362.850379, 9171.774898 / 9279.132004, 9322.791757 / 9388.136426, 9214.903676 | 12393.125, 12335.250 / 12494.500, 12607.500 / 12554.625, 12540.625 |
| StreamedSetWithDelayedSource / Connections=2 | 10723, 10805 / 16332, 16431 / 22106, 22187 | 9457.399726, 9105.046720 / 9270.851344, 9014.773197 / 9372.473834, 9024.351219 | 12398.000, 12325.875 / 12324.500, 12423.625 / 12558.750, 12450.750 |
| StreamedSetWithInstantSource / Connections=1 | 8477, 8602 / 14129, 14231 / 19862, 19963 | 180.480442, 177.512879 / 177.604876, 177.494048 / 177.285108, 178.601785 | 6293.828, 6357.908 / 6497.580, 6118.736 / 6321.414, 6342.252 |
| StreamedSetWithInstantSource / Connections=2 | 10907, 11005 / 16510, 16619 / 22265, 22389 | 182.591876, 180.717614 / 174.038134, 173.382141 / 181.899428, 181.601558 | 6083.150, 6510.959 / 6527.607, 6414.613 / 6352.984, 6094.225 |

| Phase | Connections | Stage | Samples | Managed bytes | POH bytes | POH fragmentation | Working set bytes | Private bytes |
| --- | ---: | --- | ---: | --- | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 24 | 5921672–5927960 | 8184–8184 | 0–0 | 49713152–50139136 | 134443008–134688768 |
| baseline-a | 1 | connected | 24 | 8678816–8701424 | 24528–24528 | 0–0 | 68091904–68415488 | 261222400–261382144 |
| baseline-a | 1 | after-churn | 24 | 9702056–9718464 | 24528–24528 | 0–0 | 71458816–71921664 | 263495680–280571904 |
| baseline-a | 1 | after-dispose | 24 | 8234672–8833448 | 24528–24528 | 0–0 | 75309056–103608320 | 246087680–354381824 |
| baseline-a | 2 | before-connect | 24 | 5913464–5927968 | 8184–8184 | 0–0 | 49627136–50143232 | 134447104–134676480 |
| baseline-a | 2 | connected | 24 | 10286872–10307288 | 24528–24528 | 0–0 | 69791744–70242304 | 263630848–280698880 |
| baseline-a | 2 | after-churn | 24 | 11319088–11334416 | 24528–24528 | 0–0 | 74133504–74543104 | 266838016–283992064 |
| baseline-a | 2 | after-dispose | 24 | 9003824–10060720 | 24528–24528 | 0–0 | 75849728–102842368 | 263254016–439365632 |
| candidate | 1 | before-connect | 24 | 5913600–5921928 | 8184–8184 | 0–0 | 49610752–50032640 | 134443008–134483968 |
| candidate | 1 | connected | 24 | 8677712–8693488 | 24528–24528 | 0–0 | 67915776–68403200 | 261177344–261382144 |
| candidate | 1 | after-churn | 24 | 9697664–9719112 | 24528–24528 | 0–0 | 71290880–71974912 | 263446528–280580096 |
| candidate | 1 | after-dispose | 24 | 8397952–8843872 | 24528–24528 | 0–0 | 75563008–102383616 | 247296000–456036352 |
| candidate | 2 | before-connect | 24 | 5913680–5927976 | 8184–8184 | 0–0 | 49664000–50221056 | 134443008–134684672 |
| candidate | 2 | connected | 24 | 10287056–10306520 | 24528–24528 | 0–0 | 69816320–70295552 | 263655424–280711168 |
| candidate | 2 | after-churn | 24 | 11314440–11332656 | 24528–24528 | 0–0 | 73986048–74539008 | 266854400–283934720 |
| candidate | 2 | after-dispose | 24 | 9044864–9968720 | 24528–24528 | 0–0 | 75763712–104079360 | 268722176–388780032 |
| baseline-b | 1 | before-connect | 24 | 5921672–5927960 | 8184–8184 | 0–0 | 49659904–50139136 | 134443008–134483968 |
| baseline-b | 1 | connected | 24 | 8677640–8697416 | 24528–24528 | 0–0 | 68014080–68493312 | 261189632–261451776 |
| baseline-b | 1 | after-churn | 24 | 9697520–9717704 | 24528–24528 | 0–0 | 71479296–72011776 | 263446528–280723456 |
| baseline-b | 1 | after-dispose | 24 | 8238712–9043416 | 24528–24528 | 0–0 | 75440128–105504768 | 246120448–359215104 |
| baseline-b | 2 | before-connect | 24 | 5913472–5921824 | 8184–8184 | 0–0 | 49709056–50151424 | 134447104–134684672 |
| baseline-b | 2 | connected | 24 | 10287744–10306448 | 24528–24528 | 0–0 | 69885952–70250496 | 263622656–263933952 |
| baseline-b | 2 | after-churn | 24 | 11313064–11337280 | 24528–24528 | 0–0 | 74141696–74567680 | 266842112–283975680 |
| baseline-b | 2 | after-dispose | 24 | 8968152–9995984 | 24528–24528 | 0–0 | 75599872–102789120 | 263270400–399290368 |

</details>

### Run 38195553203: error metrics enabled

Baseline `dfa7564f3946695f00c48f8d71679fdce935c8c2`; measured candidate `fbc7af5a11404398455df510a2ddb93415d05c05`; event head `ba10a10c7132e891d1bd02431a97ba31cbd9d3e5`.

<details>
<summary>Every public interval, allocation row, launch and retention group</summary>

| Case / parameters | Candidate/A; candidate/B | B/A drift | 99.9% interval us: A / candidate / B | Reported B/op: A / candidate / B |
| --- | ---: | ---: | --- | --- |
| CircuitStreamedGet / Connections=1 | -1.46%; -0.14% | -1.32% | 251.074639–258.520470 / 248.832881–253.319662 / 247.694660–255.157265 | 13044 / 14310 / 16038 |
| CircuitStreamedGet / Connections=2 | -1.55%; -1.43% | -0.13% | 253.797851–259.136171 / 249.200860–255.777665 / 252.657031–259.621857 | 14839 / 15375 / 14207 |
| ClientPing / Connections=1 | +3.40%; +0.86% | +2.52% | 105.317949–106.940530 / 109.104035–110.372522 / 108.103661–109.503192 | 184 / 184 / 184 |
| ClientPing / Connections=2 | -1.49%; -0.59% | -0.91% | 109.949906–112.100493 / 108.633726–110.099507 / 109.197329–110.828342 | 182 / 184 / 184 |
| ClientServerError / Connections=1 | +2.53%; -0.41% | +2.95% | 125.220496–126.855038 / 128.326309–130.124263 / 128.598561–130.915704 | 952 / 952 / 952 |
| ClientServerError / Connections=2 | -1.13%; +0.34% | -1.47% | 131.291669–132.681439 / 129.444284–131.548612 / 129.398346–130.701512 | 952 / 952 / 952 |
| LargeSet / Connections=1 | +6.29%; +3.31% | +2.88% | 1161.465559–1195.103876 / 1227.591611–1277.159489 / 1195.167719–1229.265094 | 177 / 176 / 177 |
| LargeSet / Connections=2 | +1.06%; +0.55% | +0.50% | 1198.492610–1251.108516 / 1215.561184–1259.956552 / 1208.888486–1253.010996 | 177 / 178 / 178 |
| Pipeline / Connections=1 | +0.80%; +1.36% | -0.55% | 4.046496–4.085535 / 4.076214–4.120694 / 4.024901–4.062409 | 7 / 7 / 7 |
| Pipeline / Connections=2 | +6.71%; +2.97% | +3.63% | 4.212029–4.327441 / 4.470843–4.641237 / 4.375559–4.473749 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | +2.13%; +0.53% | +1.59% | 4.049299–4.088298 / 4.127884–4.182998 / 4.111575–4.155395 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | +3.64%; +3.01% | +0.61% | 4.332074–4.447060 / 4.471572–4.626849 / 4.333145–4.499409 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | -65.90%; -2.66% | -64.97% | 0.003073–0.008604 / 0.001952–0.002030 / 0.002020–0.002071 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | +1.97%; -1.79% | +3.83% | 0.010198–0.010455 / 0.010506–0.010555 / 0.010607–0.010836 | 0 / 0 / 0 |
| SmallSet / Connections=1 | +1.63%; +1.80% | -0.17% | 109.015913–110.950062 / 110.511511–113.031893 / 109.052434–110.529708 | 176 / 176 / 176 |
| SmallSet / Connections=2 | +0.34%; -0.48% | +0.82% | 111.143885–112.342379 / 111.137331–113.102339 / 111.730223–113.592858 | 176 / 176 / 176 |
| StandaloneBatch / Connections=1&count=100 | +6.27%; +5.15% | +1.06% | 255.116837–266.436929 / 272.241489–282.004093 / 259.397141–267.693029 | 32407 / 32403 / 32398 |
| StandaloneBatch / Connections=1&count=32 | +1.84%; +0.47% | +1.37% | 161.977835–163.341853 / 164.613374–166.701985 / 163.751434–166.016142 | 11250 / 11246 / 11249 |
| StandaloneBatch / Connections=2&count=100 | +0.25%; -3.58% | +3.97% | 255.815449–264.992083 / 255.756818–266.342156 / 265.211981–276.254496 | 32405 / 32405 / 32404 |
| StandaloneBatch / Connections=2&count=32 | +1.53%; -0.96% | +2.51% | 160.653941–162.783059 / 163.253569–165.132900 / 163.712058–167.854728 | 11252 / 11249 / 11252 |
| StreamedSetWithDelayedSource / Connections=1 | -5.08%; -1.24% | -3.89% | 9572.085768–9688.012357 / 9086.192542–9195.845862 / 9091.367925–9420.308167 | 12335 / 12111 / 12305 |
| StreamedSetWithDelayedSource / Connections=2 | +0.51%; +0.72% | -0.21% | 9615.072673–9713.106881 / 9646.644690–9780.001152 / 9457.062733–9830.063818 | 12454 / 11986 / 12297 |
| StreamedSetWithInstantSource / Connections=1 | +2.07%; +1.71% | +0.36% | 205.503196–211.125225 / 210.197751–215.060015 / 206.297843–211.813663 | 6086 / 6364 / 6461 |
| StreamedSetWithInstantSource / Connections=2 | -1.03%; -0.43% | -0.61% | 209.918909–215.501295 / 208.172891–212.856822 / 208.443755–214.401763 | 6449 / 6289 / 6471 |

| Case / parameters | PID 1, 2: A / candidate / B | Launch 1, 2 mean us: A / candidate / B | Raw launch 1, 2 B/op: A / candidate / B |
| --- | --- | --- | --- |
| CircuitStreamedGet / Connections=1 | 9445, 9532 / 15409, 15500 / 21087, 21193 | 254.815195, 254.779914 / 250.638515, 251.455660 / 253.749816, 249.102109 | 14814.152, 13043.805 / 14381.746, 14310.418 / 14505.375, 16038.328 |
| CircuitStreamedGet / Connections=2 | 11901, 11998 / 17633, 17717 / 23415, 23499 | 255.921878, 256.975802 / 252.160706, 252.817819 / 256.994858, 255.284030 | 15017.195, 14838.695 / 14215.332, 15374.605 / 14476.094, 14206.914 |
| ClientPing / Connections=1 | 9616, 9743 / 15586, 15710 / 21340, 21461 | 106.606627, 105.651852 / 109.324951, 110.124051 / 108.997480, 108.609373 | 184.036, 184.060 / 184.028, 184.044 / 184.052, 184.044 |
| ClientPing / Connections=2 | 12092, 12214 / 17825, 17960 / 23585, 23718 | 110.266558, 111.733264 / 108.797963, 109.897360 / 109.802113, 110.223559 | 184.104, 181.948 / 184.089, 184.058 / 184.081, 184.065 |
| ClientServerError / Connections=1 | 9869, 9960 / 15837, 15919 / 21586, 21668 | 125.421819, 126.653715 / 128.486991, 129.963582 / 130.340031, 129.213094 | 952.117, 952.193 / 952.193, 952.178 / 952.193, 952.193 |
| ClientServerError / Connections=2 | 12355, 12432 / 18081, 18156 / 23844, 23920 | 131.864024, 132.109085 / 129.984347, 131.008549 / 130.337747, 129.762111 | 952.059, 952.059 / 952.283, 952.043 / 952.283, 952.189 |
| LargeSet / Connections=1 | 8905, 9010 / 14884, 14963 / 20582, 20681 | 1188.062824, 1168.506610 / 1242.259578, 1262.491522 / 1221.157254, 1203.275558 | 177.078, 177.078 / 176.719, 176.359 / 176.359, 177.078 |
| LargeSet / Connections=2 | 11373, 11453 / 17101, 17198 / 22884, 22963 | 1218.611352, 1230.989774 / 1221.029758, 1252.257430 / 1238.377387, 1223.522095 | 176.000, 177.438 / 177.797, 177.797 / 177.672, 177.797 |
| Pipeline / Connections=1 | 8295, 8381 / 14267, 14367 / 19986, 20066 | 4.057940, 4.074091 / 4.118697, 4.076765 / 4.031327, 4.055983 | 6.758, 6.747 / 6.757, 6.756 / 6.761, 6.754 |
| Pipeline / Connections=2 | 10759, 10844 / 16510, 16610 / 22292, 22373 | 4.281654, 4.257817 / 4.628124, 4.478807 / 4.463074, 4.388796 | 6.757, 6.742 / 6.754, 6.761 / 6.758, 6.766 |
| PublicPipeline / Connections=1 | 8482, 8566 / 14448, 14526 / 20149, 20251 | 4.073115, 4.064482 / 4.168521, 4.142361 / 4.134498, 4.132317 | 5.758, 5.757 / 5.735, 5.757 / 5.758, 5.753 |
| PublicPipeline / Connections=2 | 10929, 11033 / 16693, 16791 / 22434, 22516 | 4.350559, 4.428574 / 4.537826, 4.560594 / 4.426008, 4.407195 | 5.741, 5.759 / 5.733, 5.751 / 5.760, 5.758 |
| SelectConnection / Connections=1 | 7928, 8123 / 13879, 14096 / 19577, 19791 | 0.001944, 0.009733 / 0.001936, 0.002039 / 0.002036, 0.002055 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 10422, 10655 / 16326, 16426 / 22089, 22189 | 0.010157, 0.010496 / 0.010504, 0.010556 / 0.010567, 0.010865 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 8646, 8773 / 14606, 14757 / 20333, 20457 | 111.005740, 108.960235 / 113.067177, 110.476227 / 109.855242, 109.731179 | 175.999, 176.067 / 175.856, 176.060 / 176.052, 176.060 |
| SmallSet / Connections=2 | 11111, 11234 / 16854, 16974 / 22616, 22739 | 111.550295, 111.949742 / 111.652976, 112.586694 / 112.286436, 113.036644 | 176.089, 176.097 / 176.089, 175.847 / 176.104, 176.104 |
| StandaloneBatch / Connections=1&count=100 | 7754, 7842 / 13713, 13795 / 19407, 19487 | 264.660933, 256.892833 / 279.884381, 274.361201 / 265.502859, 261.727152 | 32406.180, 32406.699 / 32401.039, 32402.539 / 32409.219, 32398.219 |
| StandaloneBatch / Connections=1&count=32 | 7539, 7651 / 13524, 13590 / 19220, 19305 | 162.843438, 162.476249 / 166.224466, 165.090893 / 165.158013, 164.627844 | 11251.234, 11249.939 / 11252.020, 11246.199 / 11247.266, 11248.873 |
| StandaloneBatch / Connections=2&count=100 | 10239, 10319 / 16180, 16219 / 21907, 22008 | 262.245953, 258.429994 / 256.898748, 265.519514 / 271.648610, 269.817867 | 32406.715, 32404.848 / 32404.578, 32404.578 / 32406.188, 32404.488 |
| StandaloneBatch / Connections=2&count=32 | 10039, 10138 / 15997, 16100 / 21745, 21847 | 162.482529, 160.954470 / 164.073250, 164.313219 / 163.576313, 167.843335 | 11250.973, 11252.174 / 11248.934, 11249.414 / 11251.383, 11251.969 |
| StreamedSetWithDelayedSource / Connections=1 | 9090, 9189 / 15062, 15164 / 20760, 20840 | 9660.322293, 9599.775833 / 9073.027405, 9199.297885 / 9023.063932, 9488.612159 | 12424.000, 12335.125 / 12520.000, 12111.125 / 12236.875, 12305.125 |
| StreamedSetWithDelayedSource / Connections=2 | 11557, 11655 / 17296, 17379 / 23061, 23164 | 9677.852410, 9651.244653 / 9715.876415, 9710.769427 / 9800.274167, 9486.852384 | 12329.500, 12453.750 / 12384.375, 11985.750 / 12422.875, 12296.875 |
| StreamedSetWithInstantSource / Connections=1 | 9274, 9361 / 15245, 15327 / 20937, 21020 | 207.330949, 209.297472 / 212.353702, 212.904064 / 209.677198, 208.389920 | 6512.723, 6085.953 / 6444.934, 6363.672 / 6422.625, 6461.031 |
| StreamedSetWithInstantSource / Connections=2 | 11734, 11816 / 17481, 17568 / 23244, 23328 | 213.607391, 211.743791 / 211.155258, 209.775931 / 212.311196, 210.470862 | 6481.305, 6449.289 / 6491.195, 6288.785 / 6488.250, 6471.484 |

| Phase | Connections | Stage | Samples | Managed bytes | POH bytes | POH fragmentation | Working set bytes | Private bytes |
| --- | ---: | --- | ---: | --- | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 24 | 5913416–5921784 | 8184–8184 | 0–0 | 49655808–50094080 | 134438912–134676480 |
| baseline-a | 1 | connected | 24 | 8681288–8698520 | 24528–24528 | 0–0 | 68096000–68435968 | 261169152–261390336 |
| baseline-a | 1 | after-churn | 24 | 9701712–9718912 | 24528–24528 | 0–0 | 71589888–71921664 | 263475200–280580096 |
| baseline-a | 1 | after-dispose | 24 | 8249384–8823904 | 24528–24528 | 0–0 | 75669504–156856320 | 247853056–407916544 |
| baseline-a | 2 | before-connect | 24 | 5913440–5927936 | 8184–8184 | 0–0 | 49680384–50077696 | 134447104–134483968 |
| baseline-a | 2 | connected | 24 | 10299448–10310800 | 24528–24528 | 0–0 | 69935104–70270976 | 263659520–280788992 |
| baseline-a | 2 | after-churn | 24 | 11319480–11338072 | 24528–24528 | 0–0 | 74203136–74563584 | 267018240–283918336 |
| baseline-a | 2 | after-dispose | 24 | 8916216–9870568 | 24528–24528 | 0–0 | 75952128–157175808 | 281784320–476344320 |
| candidate | 1 | before-connect | 24 | 5913528–5928024 | 8184–8184 | 0–0 | 49680384–50061312 | 134438912–134483968 |
| candidate | 1 | connected | 24 | 8681400–8701912 | 24528–24528 | 0–0 | 68059136–68509696 | 261165056–261382144 |
| candidate | 1 | after-churn | 24 | 9699792–9718496 | 24528–24528 | 0–0 | 71528448–71999488 | 280334336–280580096 |
| candidate | 1 | after-dispose | 24 | 8246456–8787408 | 24528–24528 | 0–0 | 75636736–155693056 | 264564736–491610112 |
| candidate | 2 | before-connect | 24 | 5913656–5921904 | 8184–8184 | 0–0 | 49643520–49999872 | 134438912–134676480 |
| candidate | 2 | connected | 24 | 10287280–10310800 | 24528–24528 | 0–0 | 69906432–70299648 | 263639040–263864320 |
| candidate | 2 | after-churn | 24 | 11318400–11333488 | 24528–24528 | 0–0 | 74170368–74526720 | 283701248–283893760 |
| candidate | 2 | after-dispose | 24 | 8972544–10150240 | 24528–24528 | 0–0 | 75898880–156143616 | 281632768–423845888 |
| baseline-b | 1 | before-connect | 24 | 5913456–5927928 | 8184–8184 | 0–0 | 49680384–50016256 | 134443008–134676480 |
| baseline-b | 1 | connected | 24 | 8685520–8697936 | 24528–24528 | 0–0 | 68055040–68464640 | 261201920–261423104 |
| baseline-b | 1 | after-churn | 24 | 9701736–9739072 | 24528–24528 | 0–0 | 71512064–71954432 | 263524352–280633344 |
| baseline-b | 1 | after-dispose | 24 | 8252208–8866320 | 24528–24528 | 0–0 | 75587584–155680768 | 264642560–461164544 |
| baseline-b | 2 | before-connect | 24 | 5913424–5927832 | 8184–8184 | 0–0 | 49610752–50040832 | 134443008–134676480 |
| baseline-b | 2 | connected | 24 | 10287272–10306696 | 24528–24528 | 0–0 | 69783552–70377472 | 263651328–263839744 |
| baseline-b | 2 | after-churn | 24 | 11321280–11336904 | 24528–24528 | 0–0 | 74117120–74665984 | 283697152–283910144 |
| baseline-b | 2 | after-dispose | 24 | 9051168–10185544 | 24528–24528 | 0–0 | 74915840–157261824 | 281673728–441364480 |

</details>

### Run 38201983679: error metrics disabled

Baseline `82a6fda724b1e79f95ac3eb778547586e286b4fc`; measured candidate `f35ff1482dd5e5bcb05bf89eee71ecc61e6072a2`; event head `3c5b806bca77451f28ef8c151edebfdcdff1e392`.

<details>
<summary>Every public interval, allocation row, launch and retention group</summary>

| Case / parameters | Candidate/A; candidate/B | B/A drift | 99.9% interval us: A / candidate / B | Reported B/op: A / candidate / B |
| --- | ---: | ---: | --- | --- |
| CircuitStreamedGet / Connections=1 | +0.14%; +0.54% | -0.40% | 391.570507–399.985996 / 391.930554–400.739361 / 390.417882–398.002315 | 18605 / 18522 / 17313 |
| CircuitStreamedGet / Connections=2 | +0.86%; +1.58% | -0.71% | 374.899532–389.119044 / 380.701736–389.913803 / 376.984639–381.629117 | 19814 / 19694 / 19027 |
| ClientPing / Connections=1 | +0.20%; +0.67% | -0.46% | 188.180185–189.783726 / 188.580081–190.139108 / 187.348794–188.862495 | 183 / 184 / 184 |
| ClientPing / Connections=2 | +0.51%; +0.29% | +0.21% | 188.182333–190.033656 / 189.422341–190.715802 / 188.815121–190.205784 | 184 / 184 / 184 |
| ClientServerError / Connections=1 | +0.15%; +0.42% | -0.27% | 218.832554–220.889560 / 219.245122–221.134659 / 218.410146–220.146102 | 952 / 952 / 952 |
| ClientServerError / Connections=2 | +1.24%; +0.55% | +0.69% | 218.253704–219.711687 / 220.590977–222.826541 / 219.555983–221.442660 | 952 / 952 / 952 |
| LargeSet / Connections=1 | +3.71%; +2.76% | +0.93% | 1774.531131–1884.601313 / 1842.664878–1952.235750 / 1790.186476–1902.966124 | 176 / 177 / 176 |
| LargeSet / Connections=2 | +1.38%; +1.73% | -0.34% | 1773.020454–1864.377695 / 1786.905879–1900.738102 / 1754.112768–1870.740064 | 179 / 176 / 176 |
| Pipeline / Connections=1 | +0.52%; +0.43% | +0.09% | 6.631080–6.707585 / 6.674627–6.732863 / 6.655904–6.694525 | 7 / 7 / 7 |
| Pipeline / Connections=2 | +0.61%; +1.44% | -0.82% | 7.638713–7.718824 / 7.682373–7.768315 / 7.538086–7.692983 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | +0.30%; +0.09% | +0.21% | 6.682864–6.731101 / 6.695382–6.759035 / 6.692580–6.749069 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | +1.04%; +1.55% | -0.50% | 7.634906–7.760134 / 7.731250–7.823937 / 7.599734–7.718467 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | +0.32%; +0.53% | -0.21% | 0.002642–0.002665 / 0.002651–0.002673 / 0.002641–0.002655 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | +0.02%; +0.14% | -0.12% | 0.006377–0.006433 / 0.006384–0.006429 / 0.006381–0.006414 | 0 / 0 / 0 |
| SmallSet / Connections=1 | +0.18%; -0.03% | +0.21% | 192.598577–194.060125 / 192.661463–194.691659 / 193.073797–194.392078 | 176 / 176 / 176 |
| SmallSet / Connections=2 | +1.20%; +1.35% | -0.15% | 192.516853–193.933054 / 195.051504–196.040203 / 192.287596–193.599895 | 176 / 176 / 176 |
| StandaloneBatch / Connections=1&count=100 | +0.11%; +0.24% | -0.14% | 357.261345–361.025640 / 357.713909–361.348625 / 357.060016–360.249842 | 32398 / 32414 / 32408 |
| StandaloneBatch / Connections=1&count=32 | +0.32%; +0.16% | +0.16% | 254.317698–256.055024 / 255.138197–256.869304 / 254.868212–256.330387 | 11251 / 11249 / 11251 |
| StandaloneBatch / Connections=2&count=100 | +0.27%; +1.03% | -0.75% | 355.777862–360.071796 / 357.595773–360.184639 / 353.785763–356.674448 | 32410 / 32405 / 32405 |
| StandaloneBatch / Connections=2&count=32 | +0.22%; +0.88% | -0.65% | 255.227096–256.639296 / 255.819740–257.192313 / 253.205679–255.329734 | 11253 / 11251 / 11248 |
| StreamedSetWithDelayedSource / Connections=1 | +0.64%; +2.67% | -1.97% | 10045.195842–10159.103499 / 10093.745167–10240.159812 / 9857.580407–9948.275675 | 12396 / 12628 / 12396 |
| StreamedSetWithDelayedSource / Connections=2 | +1.44%; +3.94% | -2.40% | 10000.222380–10118.057372 / 10133.532360–10274.091183 / 9741.874498–9892.958608 | 12498 / 12684 / 12275 |
| StreamedSetWithInstantSource / Connections=1 | +4.50%; +2.45% | +2.01% | 361.581966–368.763271 / 370.356858–392.877700 / 367.266347–377.734203 | 6452 / 5953 / 6477 |
| StreamedSetWithInstantSource / Connections=2 | -1.46%; +1.73% | -3.14% | 360.026602–391.490015 / 365.630170–374.926522 / 359.833740–368.096673 | 6483 / 5914 / 6467 |

| Case / parameters | PID 1, 2: A / candidate / B | Launch 1, 2 mean us: A / candidate / B | Raw launch 1, 2 B/op: A / candidate / B |
| --- | --- | --- | --- |
| CircuitStreamedGet / Connections=1 | 9709, 9779 / 15489, 15560 / 21518, 21586 | 398.105701, 393.284556 / 394.085690, 398.584225 / 395.300882, 393.264753 | 19310.664, 18604.688 / 18330.891, 18522.086 / 16551.516, 17312.766 |
| CircuitStreamedGet / Connections=2 | 11951, 12015 / 17869, 17938 / 23854, 23970 | 384.513229, 379.505347 / 383.218425, 387.397113 / 378.823699, 379.830322 | 19719.734, 19813.562 / 19727.383, 19693.570 / 20135.957, 19027.016 |
| ClientPing / Connections=1 | 9872, 9977 / 15649, 15774 / 21711, 21838 | 189.132428, 188.831483 / 189.895075, 188.824114 / 188.012842, 188.198446 | 184.104, 182.541 / 184.057, 184.059 / 183.748, 184.090 |
| ClientPing / Connections=2 | 12101, 12213 / 18079, 18179 / 24052, 24151 | 189.017840, 189.198149 / 190.412390, 189.725753 / 189.255685, 189.783417 | 184.117, 183.646 / 184.225, 184.193 / 184.225, 183.990 |
| ClientServerError / Connections=1 | 10103, 10231 / 15878, 16010 / 21940, 22063 | 220.279716, 219.442399 / 220.733602, 219.646178 / 218.307801, 220.119071 | 952.193, 952.355 / 952.146, 952.387 / 952.193, 952.387 |
| ClientServerError / Connections=2 | 12347, 12467 / 18302, 18429 / 24275, 24409 | 219.130655, 218.834737 / 222.136680, 221.280837 / 220.104302, 220.868006 | 952.283, 952.158 / 952.238, 952.020 / 952.104, 952.088 |
| LargeSet / Connections=1 | 9182, 9244 / 14858, 14982 / 20949, 21023 | 1846.198132, 1811.746318 / 1899.422123, 1895.478505 / 1833.851158, 1859.301442 | 176.250, 176.000 / 177.078, 177.438 / 176.000, 176.000 |
| LargeSet / Connections=2 | 11424, 11507 / 17273, 17351 / 23331, 23408 | 1825.465798, 1811.449015 / 1850.810930, 1836.833051 / 1829.057664, 1795.795168 | 176.000, 178.875 / 176.000, 176.000 / 176.000, 176.000 |
| Pipeline / Connections=1 | 8521, 8580 / 14320, 14405 / 20382, 20442 | 6.688329, 6.648979 / 6.725167, 6.682323 / 6.668597, 6.682305 | 6.765, 6.767 / 6.765, 6.764 / 6.767, 6.764 |
| Pipeline / Connections=2 | 10855, 10938 / 16726, 16803 / 22782, 22869 | 7.662132, 7.694296 / 7.753725, 7.696963 / 7.621739, 7.609330 | 6.801, 6.767 / 6.757, 6.760 / 6.771, 6.767 |
| PublicPipeline / Connections=1 | 8726, 8811 / 14470, 14550 / 20565, 20646 | 6.725894, 6.689332 / 6.726248, 6.728169 / 6.708553, 6.731460 | 5.772, 5.757 / 5.768, 5.770 / 5.770, 5.770 |
| PublicPipeline / Connections=2 | 11022, 11122 / 16885, 16967 / 22950, 23031 | 7.700280, 7.694760 / 7.770213, 7.784447 / 7.631280, 7.683210 | 5.737, 5.752 / 5.773, 5.776 / 5.749, 5.746 |
| SelectConnection / Connections=1 | 8181, 8352 / 14003, 14152 / 20065, 20215 | 0.002647, 0.002660 / 0.002660, 0.002664 / 0.002655, 0.002640 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 10621, 10735 / 16478, 16600 / 22532, 22656 | 0.006410, 0.006399 / 0.006390, 0.006422 / 0.006387, 0.006409 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 8934, 9050 / 14631, 14735 / 20724, 20828 | 193.047204, 193.592688 / 193.151729, 194.201393 / 194.168987, 193.296888 | 176.088, 176.119 / 176.119, 176.135 / 176.119, 176.119 |
| SmallSet / Connections=2 | 11203, 11307 / 17048, 17171 / 23109, 23209 | 192.701357, 193.748550 / 195.607369, 195.488439 / 192.702799, 193.184692 | 176.193, 176.193 / 176.000, 176.045 / 176.209, 176.193 |
| StandaloneBatch / Connections=1&count=100 | 7940, 8071 / 13811, 13873 / 19878, 19940 | 357.580069, 360.818589 / 359.190048, 359.924982 / 358.240996, 359.041267 | 32403.898, 32398.469 / 32408.398, 32414.488 / 32404.156, 32408.457 |
| StandaloneBatch / Connections=1&count=32 | 7770, 7860 / 13641, 13730 / 19685, 19797 | 255.751688, 254.621034 / 255.345277, 256.618326 / 255.055644, 256.106712 | 11248.227, 11251.008 / 11252.527, 11248.539 / 11250.477, 11251.039 |
| StandaloneBatch / Connections=2&count=100 | 10477, 10538 / 16253, 16374 / 22306, 22430 | 356.838439, 359.011219 / 358.362539, 359.417873 / 355.908890, 354.499108 | 32410.938, 32409.617 / 32410.277, 32404.566 / 32387.395, 32404.707 |
| StandaloneBatch / Connections=2&count=32 | 10294, 10395 / 16093, 16174 / 22144, 22223 | 255.820248, 256.046143 / 256.366242, 256.635826 / 255.261673, 253.273740 | 11247.738, 11252.988 / 11252.488, 11250.828 / 11250.707, 11247.707 |
| StreamedSetWithDelayedSource / Connections=1 | 9322, 9420 / 15062, 15161 / 21104, 21183 | 10097.904068, 10106.395274 / 10156.364733, 10177.540246 / 9944.240556, 9864.369693 | 12398.125, 12396.000 / 12734.750, 12628.000 / 12275.875, 12395.750 |
| StreamedSetWithDelayedSource / Connections=2 | 11566, 11665 / 17434, 17540 / 23488, 23566 | 10020.256329, 10098.023423 / 10211.167059, 10196.946836 / 9899.483255, 9735.349851 | 12404.000, 12497.750 / 12467.375, 12683.625 / 12499.500, 12275.250 |
| StreamedSetWithInstantSource / Connections=1 | 9523, 9582 / 15263, 15364 / 21286, 21411 | 363.764702, 366.392812 / 366.668770, 396.565788 / 377.070787, 367.929763 | 6466.680, 6452.430 / 5909.801, 5952.785 / 6499.035, 6477.141 |
| StreamedSetWithInstantSource / Connections=2 | 11764, 11869 / 17639, 17743 / 23667, 23771 | 363.580212, 387.936405 / 372.109666, 368.447026 / 365.169809, 362.575280 | 6490.352, 6483.141 / 5946.414, 5913.707 / 6481.000, 6467.195 |

| Phase | Connections | Stage | Samples | Managed bytes | POH bytes | POH fragmentation | Working set bytes | Private bytes |
| --- | ---: | --- | ---: | --- | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 24 | 5911808–5926168 | 8184–8184 | 0–0 | 49524736–49938432 | 134430720–134672384 |
| baseline-a | 1 | connected | 24 | 8679136–8691592 | 24528–24528 | 0–0 | 67964928–68403200 | 261201920–261378048 |
| baseline-a | 1 | after-churn | 24 | 9700696–9716840 | 24528–24528 | 0–0 | 71389184–71872512 | 280383488–280559616 |
| baseline-a | 1 | after-dispose | 24 | 8247232–8971936 | 24528–24528 | 0–0 | 75149312–102453248 | 246300672–474378240 |
| baseline-a | 2 | before-connect | 24 | 5911688–5926200 | 8184–8184 | 0–0 | 49582080–49905664 | 134434816–134672384 |
| baseline-a | 2 | connected | 24 | 10288096–10304720 | 24528–24528 | 0–0 | 69853184–70258688 | 263675904–280735744 |
| baseline-a | 2 | after-churn | 24 | 11318152–11339248 | 24528–24528 | 0–0 | 74133504–74534912 | 283701248–283938816 |
| baseline-a | 2 | after-dispose | 24 | 9011528–9865784 | 24528–24528 | 0–0 | 75120640–103260160 | 263725056–456482816 |
| candidate | 1 | before-connect | 24 | 5911776–5926288 | 8184–8184 | 0–0 | 49508352–49864704 | 134434816–134672384 |
| candidate | 1 | connected | 24 | 8675944–8695728 | 24528–24528 | 0–0 | 67821568–68313088 | 261197824–261337088 |
| candidate | 1 | after-churn | 24 | 9703520–9721776 | 24528–24528 | 0–0 | 71270400–71823360 | 280367104–280580096 |
| candidate | 1 | after-dispose | 24 | 8241616–9043512 | 24528–24528 | 0–0 | 75075584–102608896 | 246079488–473722880 |
| candidate | 2 | before-connect | 24 | 5911928–5926264 | 8184–8184 | 0–0 | 49565696–50008064 | 134430720–134672384 |
| candidate | 2 | connected | 24 | 10288128–10304912 | 24528–24528 | 0–0 | 69779456–70201344 | 263630848–263880704 |
| candidate | 2 | after-churn | 24 | 11315992–11342384 | 24528–24528 | 0–0 | 74059776–74498048 | 283676672–283947008 |
| candidate | 2 | after-dispose | 24 | 8963992–9932536 | 24528–24528 | 0–0 | 74620928–103084032 | 263340032–474173440 |
| baseline-b | 1 | before-connect | 24 | 5911680–5926192 | 8184–8184 | 0–0 | 49532928–49934336 | 134434816–134475776 |
| baseline-b | 1 | connected | 24 | 8679128–8691560 | 24528–24528 | 0–0 | 68018176–68333568 | 261214208–261386240 |
| baseline-b | 1 | after-churn | 24 | 9703016–9720904 | 24528–24528 | 0–0 | 71540736–71868416 | 263528448–280547328 |
| baseline-b | 1 | after-dispose | 24 | 8237864–8847384 | 24528–24528 | 0–0 | 75284480–105885696 | 246181888–393408512 |
| baseline-b | 2 | before-connect | 24 | 5911832–5926096 | 8184–8184 | 0–0 | 49528832–49917952 | 134434816–134475776 |
| baseline-b | 2 | connected | 24 | 10285208–10304744 | 24528–24528 | 0–0 | 69750784–70180864 | 263663616–280702976 |
| baseline-b | 2 | after-churn | 24 | 11316064–11335032 | 24528–24528 | 0–0 | 73977856–74473472 | 266956800–283926528 |
| baseline-b | 2 | after-dispose | 24 | 8967664–10046968 | 24528–24528 | 0–0 | 75300864–103763968 | 263684096–442585088 |

</details>

### Run 38201983679: error metrics enabled

Baseline `82a6fda724b1e79f95ac3eb778547586e286b4fc`; measured candidate `f35ff1482dd5e5bcb05bf89eee71ecc61e6072a2`; event head `3c5b806bca77451f28ef8c151edebfdcdff1e392`.

<details>
<summary>Every public interval, allocation row, launch and retention group</summary>

| Case / parameters | Candidate/A; candidate/B | B/A drift | 99.9% interval us: A / candidate / B | Reported B/op: A / candidate / B |
| --- | ---: | ---: | --- | --- |
| CircuitStreamedGet / Connections=1 | +2.02%; +0.13% | +1.89% | 180.201523–186.039709 / 182.095451–191.551443 / 181.738104–191.421362 | 22690 / 24192 / 23382 |
| CircuitStreamedGet / Connections=2 | -1.06%; +0.50% | -1.56% | 191.653179–198.849089 / 190.099728–196.254297 / 188.596954–195.829041 | 22718 / 22095 / 23328 |
| ClientPing / Connections=1 | -1.86%; +1.55% | -3.36% | 68.757873–70.861447 / 67.608974–69.407483 / 66.212048–68.711426 | 182 / 183 / 184 |
| ClientPing / Connections=2 | +1.19%; +2.01% | -0.80% | 67.086442–69.294878 / 68.280046–69.729230 / 66.199860–69.095447 | 177 / 180 / 182 |
| ClientServerError / Connections=1 | +0.15%; +0.52% | -0.37% | 78.713376–81.000982 / 78.554593–81.400193 / 78.120214–81.002342 | 951 / 951 / 951 |
| ClientServerError / Connections=2 | +1.90%; +1.03% | +0.86% | 76.916435–79.732621 / 78.542554–81.084535 / 77.856513–80.137277 | 951 / 952 / 952 |
| LargeSet / Connections=1 | -5.29%; +3.30% | -8.31% | 935.100844–993.124105 / 901.087816–925.148251 / 873.075555–894.889926 | 177 / 177 / 177 |
| LargeSet / Connections=2 | -0.34%; +1.01% | -1.34% | 909.621851–944.192121 / 916.444158–931.035909 / 906.119558–922.910389 | 176 / 176 / 177 |
| Pipeline / Connections=1 | +1.63%; +4.11% | -2.38% | 2.739437–2.951699 / 2.813813–2.970009 / 2.674204–2.881403 | 7 / 7 / 7 |
| Pipeline / Connections=2 | -0.31%; +2.48% | -2.73% | 3.598684–3.733374 / 3.592980–3.716055 / 3.506479–3.625720 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | -3.79%; +2.58% | -6.21% | 2.965331–3.106030 / 2.850105–2.990954 / 2.756769–2.937518 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | +0.90%; +1.80% | -0.88% | 3.614703–3.725635 / 3.651917–3.754828 / 3.575212–3.700259 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | +7.14%; -14.28% | +25.00% | 0.000887–0.000957 / 0.000943–0.001033 / 0.001119–0.001187 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | -0.44%; -0.58% | +0.14% | 0.006509–0.006680 / 0.006452–0.006680 / 0.006522–0.006686 | 0 / 0 / 0 |
| SmallSet / Connections=1 | -1.39%; +2.16% | -3.48% | 69.270115–71.111203 / 68.185141–70.243451 / 66.667274–68.834618 | 176 / 176 / 174 |
| SmallSet / Connections=2 | -0.56%; +2.40% | -2.89% | 69.581519–71.917638 / 69.715633–70.990875 / 67.830540–69.574400 | 175 / 176 / 175 |
| StandaloneBatch / Connections=1&count=100 | -0.37%; +1.39% | -1.74% | 161.931436–165.270043 / 161.261921–164.712875 / 159.458959–162.061231 | 32417 / 32422 / 32420 |
| StandaloneBatch / Connections=1&count=32 | +0.14%; +0.49% | -0.35% | 109.481542–112.741222 / 109.480872–113.051292 / 109.598011–111.841134 | 11247 / 11242 / 11247 |
| StandaloneBatch / Connections=2&count=100 | +4.83%; +2.00% | +2.77% | 159.210713–162.501678 / 167.287677–169.951813 / 163.208536–167.413741 | 32418 / 32415 / 32419 |
| StandaloneBatch / Connections=2&count=32 | -3.21%; -3.58% | +0.39% | 114.795459–116.762965 / 110.181626–113.949656 / 115.418295–117.045106 | 11251 / 11250 / 11251 |
| StreamedSetWithDelayedSource / Connections=1 | +0.76%; +2.01% | -1.23% | 9055.722371–9331.681183 / 9139.216408–9387.942719 / 8925.024240–9236.295160 | 12420 / 12775 / 12648 |
| StreamedSetWithDelayedSource / Connections=2 | -2.40%; -2.57% | +0.17% | 9233.206521–9327.061862 / 8912.461773–9202.570590 / 9234.206956–9357.729149 | 12547 / 12759 / 12544 |
| StreamedSetWithInstantSource / Connections=1 | -0.43%; -0.38% | -0.04% | 166.618617–172.965551 / 166.255572–171.880515 / 166.996102–172.437108 | 6363 / 5979 / 6483 |
| StreamedSetWithInstantSource / Connections=2 | -3.31%; -1.17% | -2.17% | 174.182914–178.021484 / 168.178333–172.357922 / 169.849270–174.719680 | 6518 / 5690 / 6387 |

| Case / parameters | PID 1, 2: A / candidate / B | Launch 1, 2 mean us: A / candidate / B | Raw launch 1, 2 B/op: A / candidate / B |
| --- | --- | --- | --- |
| CircuitStreamedGet / Connections=1 | 8712, 8826 / 14423, 14503 / 20131, 20252 | 183.361083, 182.896180 / 187.695437, 186.009590 / 191.807450, 181.700530 | 23106.215, 22690.404 / 24140.023, 24191.857 / 22704.930, 23382.369 |
| CircuitStreamedGet / Connections=2 | 11091, 11190 / 16786, 16861 / 22544, 22647 | 195.828110, 194.674159 / 195.676295, 190.844349 / 195.228693, 189.398349 | 23915.709, 22717.867 / 21078.051, 22094.643 / 22748.037, 23327.836 |
| ClientPing / Connections=1 | 8960, 9043 / 14609, 14688 / 20365, 20444 | 69.031492, 70.532246 / 68.568532, 68.443286 / 67.634673, 67.288801 | 183.341, 182.489 / 183.544, 182.528 / 183.383, 183.708 |
| ClientPing / Connections=2 | 11329, 11411 / 16998, 17077 / 22786, 22867 | 69.086486, 67.157015 / 68.650778, 69.358498 / 66.987530, 68.354929 | 174.556, 177.470 / 182.648, 179.921 / 183.500, 182.422 |
| ClientServerError / Connections=1 | 9123, 9224 / 14785, 14866 / 20542, 20627 | 79.554003, 80.160355 / 80.729774, 79.171271 / 79.752584, 79.369972 | 951.881, 951.356 / 951.508, 950.536 / 951.769, 950.692 |
| ClientServerError / Connections=2 | 11512, 11594 / 17157, 17259 / 22945, 23041 | 78.148533, 78.500523 / 79.727319, 79.899770 / 78.644525, 79.374434 | 950.470, 950.704 / 951.713, 951.787 / 952.048, 951.576 |
| LargeSet / Connections=1 | 8085, 8169 / 13834, 13914 / 19567, 19625 | 975.689253, 952.535696 / 910.621557, 915.281647 / 875.518537, 893.749130 | 177.078, 177.078 / 176.000, 176.539 / 177.078, 176.539 |
| LargeSet / Connections=2 | 10484, 10626 / 16202, 16320 / 21951, 22008 | 916.033709, 938.556925 / 926.515256, 920.964810 / 915.724468, 913.119404 | 176.898, 176.000 / 176.898, 176.000 / 176.359, 176.898 |
| Pipeline / Connections=1 | 7542, 7667 / 13340, 13464 / 19019, 19136 | 2.833185, 2.857950 / 2.836522, 2.939914 / 2.707290, 2.853353 | 6.706, 6.746 / 6.754, 6.758 / 6.748, 6.747 |
| Pipeline / Connections=2 | 9938, 10073 / 15592, 15675 / 21360, 21485 | 3.638688, 3.693370 / 3.630085, 3.678950 / 3.561747, 3.570452 | 6.523, 6.509 / 6.653, 6.555 / 6.643, 6.677 |
| PublicPipeline / Connections=1 | 7775, 7840 / 13527, 13590 / 19215, 19318 | 3.086481, 2.984879 / 2.916741, 2.924588 / 2.888324, 2.808707 | 5.750, 5.721 / 5.741, 5.754 / 5.753, 5.707 |
| PublicPipeline / Connections=2 | 10155, 10238 / 15752, 15895 / 21565, 21688 | 3.637884, 3.702454 / 3.735692, 3.666081 / 3.612535, 3.666813 | 5.633, 5.700 / 5.595, 5.654 / 5.509, 5.586 |
| SelectConnection / Connections=1 | 7172, 7372 / 13005, 13172 / 18659, 18848 | 0.000885, 0.000957 / 0.001014, 0.000962 / 0.001134, 0.001170 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 9738, 9839 / 15377, 15487 / 21156, 21259 | 0.006637, 0.006555 / 0.006588, 0.006545 / 0.006624, 0.006581 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 7918, 8005 / 13669, 13754 / 19380, 19465 | 70.171943, 70.208127 / 69.477098, 68.932723 / 67.648649, 67.860550 | 175.185, 175.564 / 175.317, 175.692 / 168.976, 174.248 |
| SmallSet / Connections=2 | 10318, 10402 / 16019, 16121 / 21770, 21849 | 70.285665, 71.284863 / 70.757729, 69.919887 / 68.244967, 69.159973 | 175.784, 175.492 / 175.069, 175.758 / 175.917, 175.034 |
| StandaloneBatch / Connections=1&count=100 | 6974, 7084 / 12801, 12903 / 18455, 18559 | 165.079759, 162.121720 / 161.310176, 164.552805 / 161.707210, 159.655128 | 32414.730, 32416.902 / 32409.732, 32421.633 / 32420.133, 32419.953 |
| StandaloneBatch / Connections=1&count=32 | 6658, 6797 / 12525, 12655 / 18201, 18330 | 110.843816, 111.361110 / 112.232570, 110.299595 / 111.169321, 110.269824 | 11246.277, 11247.462 / 11252.113, 11242.077 / 11252.598, 11247.171 |
| StandaloneBatch / Connections=2&count=100 | 9534, 9635 / 15168, 15268 / 20959, 21057 | 161.600034, 160.055140 / 168.097208, 169.142282 / 165.015270, 165.607007 | 32420.764, 32417.959 / 32418.934, 32414.947 / 32418.500, 32419.148 |
| StandaloneBatch / Connections=2&count=32 | 9324, 9407 / 14967, 15044 / 20731, 20809 | 115.714104, 115.844320 / 110.265515, 113.865767 / 116.290253, 116.173147 | 11252.707, 11251.180 / 11248.881, 11250.242 / 11251.740, 11251.227 |
| StreamedSetWithDelayedSource / Connections=1 | 8247, 8329 / 14037, 14141 / 19745, 19845 | 9035.874173, 9351.529381 / 9376.837215, 9122.007499 / 9052.021842, 9109.297558 | 12330.625, 12420.125 / 12719.750, 12774.625 / 12520.875, 12648.500 |
| StreamedSetWithDelayedSource / Connections=2 | 10704, 10783 / 16400, 16480 / 22158, 22237 | 9294.702688, 9265.565695 / 9097.366593, 9022.979159 / 9321.376082, 9268.745164 | 12827.500, 12547.375 / 12684.000, 12758.625 / 12833.250, 12544.250 |
| StreamedSetWithInstantSource / Connections=1 | 8436, 8604 / 14220, 14323 / 19925, 20027 | 167.530970, 172.214707 / 171.117212, 166.872506 / 172.004332, 167.428877 | 6319.996, 6363.227 / 5821.494, 5978.631 / 6434.893, 6483.088 |
| StreamedSetWithInstantSource / Connections=2 | 10862, 10981 / 16578, 16685 / 22333, 22437 | 175.923757, 176.280641 / 170.559656, 169.955776 / 172.593371, 171.996171 | 6425.463, 6517.625 / 5783.312, 5690.318 / 6457.645, 6386.902 |

| Phase | Connections | Stage | Samples | Managed bytes | POH bytes | POH fragmentation | Working set bytes | Private bytes |
| --- | ---: | --- | ---: | --- | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 24 | 5920232–5926392 | 8184–8184 | 0–0 | 49672192–50110464 | 134434816–134672384 |
| baseline-a | 1 | connected | 24 | 8675816–8700392 | 24528–24528 | 0–0 | 68005888–68558848 | 261173248–261341184 |
| baseline-a | 1 | after-churn | 24 | 9701472–9717376 | 24528–24528 | 0–0 | 71475200–72105984 | 263499776–280621056 |
| baseline-a | 1 | after-dispose | 24 | 8244664–8844888 | 24528–24528 | 0–0 | 75493376–102322176 | 246202368–406740992 |
| baseline-a | 2 | before-connect | 24 | 5920240–5926400 | 8184–8184 | 0–0 | 49672192–49991680 | 134430720–134672384 |
| baseline-a | 2 | connected | 24 | 10288488–10305496 | 24528–24528 | 0–0 | 69890048–70152192 | 263606272–263827456 |
| baseline-a | 2 | after-churn | 24 | 11316904–11335288 | 24528–24528 | 0–0 | 74174464–74547200 | 266739712–283877376 |
| baseline-a | 2 | after-dispose | 24 | 9061688–9992696 | 24528–24528 | 0–0 | 75468800–105242624 | 263376896–425111552 |
| candidate | 1 | before-connect | 24 | 5912104–5926584 | 8184–8184 | 0–0 | 49684480–50135040 | 134434816–134479872 |
| candidate | 1 | connected | 24 | 8679976–8692408 | 24528–24528 | 0–0 | 68055040–68505600 | 261169152–261419008 |
| candidate | 1 | after-churn | 24 | 9698152–9715256 | 24528–24528 | 0–0 | 71512064–71995392 | 263479296–280625152 |
| candidate | 1 | after-dispose | 24 | 8243664–8830376 | 24528–24528 | 0–0 | 75448320–101957632 | 262733824–355246080 |
| candidate | 2 | before-connect | 24 | 5912112–5926520 | 8184–8184 | 0–0 | 49680384–49979392 | 134434816–134672384 |
| candidate | 2 | connected | 24 | 10288896–10305720 | 24528–24528 | 0–0 | 69902336–70270976 | 263581696–263819264 |
| candidate | 2 | after-churn | 24 | 11315328–11336368 | 24528–24528 | 0–0 | 74129408–74510336 | 266833920–283889664 |
| candidate | 2 | after-dispose | 24 | 9032456–10000904 | 24528–24528 | 0–0 | 75726848–107540480 | 263450624–527048704 |
| baseline-b | 1 | before-connect | 24 | 5920232–5926392 | 8184–8184 | 0–0 | 49639424–50016256 | 134438912–134672384 |
| baseline-b | 1 | connected | 24 | 8675888–8696512 | 24528–24528 | 0–0 | 67997696–68509696 | 261181440–261398528 |
| baseline-b | 1 | after-churn | 24 | 9633128–9715048 | 24528–24528 | 0–0 | 71385088–71962624 | 263426048–280649728 |
| baseline-b | 1 | after-dispose | 24 | 8237272–9036064 | 24528–24528 | 0–0 | 75272192–102924288 | 246317056–473088000 |
| baseline-b | 2 | before-connect | 24 | 5912136–5920384 | 8184–8184 | 0–0 | 49618944–49950720 | 134434816–134672384 |
| baseline-b | 2 | connected | 24 | 10285864–10305496 | 24528–24528 | 0–0 | 69668864–70307840 | 263565312–280571904 |
| baseline-b | 2 | after-churn | 24 | 11315552–11343160 | 24528–24528 | 0–0 | 74035200–74596352 | 266809344–283873280 |
| baseline-b | 2 | after-dispose | 24 | 9037640–9903216 | 24528–24528 | 0–0 | 75468800–104402944 | 263094272–456945664 |

</details>

## Evidence audit: 2026-10-12

### Pinned final-frame wait comparison

**Aggregate transport performance acceptance remains unmet.** This audit completes the evidence disposition requested by [#1433](https://github.com/thomhurst/Respire/issues/1433). Further causal measurement and any resulting implementation are tracked in native child [#1474](https://github.com/thomhurst/Respire/issues/1474), which blocks [#1214](https://github.com/thomhurst/Respire/issues/1214). Both #1214 and [#862](https://github.com/thomhurst/Respire/issues/862) remain open. The merged write-outcome foundation and the two pooling fixes do not need reimplementation. No unchanged comparison was rerun for this audit.

[Run 38141963234](https://github.com/thomhurst/Respire/actions/runs/38141963234) measures [PR #1434](https://github.com/thomhurst/Respire/pull/1434). Both baseline controls pin `3a6d7ef79707148eaa85dbc8106bea7d572e8b2e`; the candidate pins immutable merge `5fd8e478730a2df7ded8f9743306304382e1c426`, whose second parent is implementation head `0d882ced4c8e33b8b82ad3aa3e0dac65cb570deb`. The metadata head is the implementation head, not the measured merge. The artifacts are `transport-acceptance-comparison-disabled` (ID 11704970497) and `transport-acceptance-comparison-enabled` (ID 11704342219).

Each metric mode brackets A/candidate/B on its own Ubuntu 24.04 Linux x64 runner, with SDK 10.0.401, runtime 10.0.12, BenchmarkDotNet 0.15.8, concurrent workstation GC and digest-pinned Redis 8.10. Disabled uses Intel Xeon Platinum 8370C; enabled uses Intel Xeon Platinum 8573C. Each has four logical/two physical cores. Absolute values across modes cannot identify metric cost: the machines differ. High-priority setup fails with permission denied in every phase.

All 48 case rows, 288 measured launches, 4,320 raw actual workload samples, 4,213 retained result samples, 96 Dry validation launches and 1,536 full-GC samples were checked. Every measured launch has 15 actual iterations; each case retains 24–30 results across its two launches after outlier handling. Operation counts agree within each launch's actual/result samples and native exits are successful. Every phase publishes the requested error-metric instrument. The complete intervals, allocation reports, launch means, raw GC ratios, control drift and retention groups follow below. Workflow success only proves completed measurements.

The fixture adds circuit-enabled streamed GET to the previous audit's cases. It constructs a public client, a circuit-enabled client and an internal multiplexer, then warms instant SET and circuit GET during setup for every case. `Connections` is not the total number of process connections. All cases still retain the 5 MiB caller payload and two 256 KiB source arrays. Batch units are whole batches; raw/public pipeline units are individual commands. Large SET uses the public array-backed gathered route, already present in both controls. Its copying/gather-send performance is separately tracked by [#976](https://github.com/thomhurst/Respire/issues/976).

The baseline/candidate production diff is exactly the pooling builder attribute on `WaitForFinalFrameWriteAsync`; the other changed files are tests. Batch, selection, stream reader and fixture source are identical across the bracket. The measured merge predates later changes on main, including batch route handling. It is evidence for these pinned revisions, not a benchmark of all current main, TLS, Windows or net8.0.

### Public latency disposition

No public row has a slower interval separated from **both** controls. This does not establish equivalence. Disabled two-connection PING, one-connection public pipeline and one-connection delayed SET are slower with separated intervals against B only. Disabled two-connection small SET is faster against B only, and two-connection delayed SET is faster against both. Enabled two-connection circuit GET is slower against A only; two-connection small SET is slower against B only; two-connection PING, one-connection public pipeline and two-connection instant SET are faster against B only. Every other public row overlaps both controls. Internal disabled one-connection selection is slower against both; disabled two-connection selection is faster against both. All internal pipeline and enabled selection intervals overlap both controls.

Enabled circuit GET with two connections is +11.95%/+8.08% versus A/B. Its candidate interval is 223.319–263.323 us, compared with A 213.105–221.602 us and B 221.917–228.345 us. Candidate launch means are 220.570/266.072 us versus A 217.189/217.518 and B 227.724/222.352. Disabled GET has its own different second-launch regime, and B has 342.151/275.031 us launch means. Circuit GET does not execute the changed SET wait during its timed region; its setup does warm SET. These process regimes must remain visible and need causal diagnosis in #1474. They cannot establish a general GET regression or disappear because an aggregate interval overlaps B.

### Allocation causes and measurement boundary

The exported allocation report is not an average across launches. [BenchmarkDotNet 0.15.8's report constructor](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.8/src/BenchmarkDotNet/Reports/BenchmarkReport.cs#L46) selects the last execution's GC statistics. Its [GC statistics implementation](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.8/src/BenchmarkDotNet/Engines/GcStats.cs#L37) rounds bytes per operation, and on modern .NET reads `GC.GetTotalAllocatedBytes(precise: true)`. Thus allocations include process background work, rather than only the invoking thread or the changed helper. This explains why an integer report can look restored while launch 1 remains high. It does not identify the allocator responsible for that high launch.

Instant SET with one connection remains mixed. Disabled A/C/B reports are 6437/6463/6230 B/op, with candidate launches 6501.844/6462.637 versus A 6327.828/6437.324 and B 6485.004/6229.949. Both candidate launches exceed both A launches; candidate launch 1 also exceeds both B launches. Enabled reports are 6434/6481/6498, but candidate launch 1 is 6508.283 versus A 6428.850/6434.176 and B 6508.834/6497.826. Both two-connection candidate instant launches are below every corresponding control launch in both modes. This supports a particular observed reduction, not a consistent restored contract for every profile.

Delayed SET also remains mixed: disabled reports are 12332/12364/12517 and 12358/12437/12297 for one/two connections; enabled reports are 12304/12431/12237 and 12593/12277/12371. Enabled/two-connection candidate launches are below all controls, but enabled/one-connection launch 2 exceeds both A's launch 2 and B's launch 2. Disabled/two-connection launches exceed both B launches. The earlier pooled outer state machines' reduction and the isolated zero-byte final-wait regression do not imply a fixed total upload allocation.

There are concrete remaining allocation sites in the identical [stream reader](https://github.com/thomhurst/Respire/blob/5fd8e478730a2df7ded8f9743306304382e1c426/src/Respire/Networking/RespireConnection.StreamPayloadReader.cs) and [streaming sender](https://github.com/thomhurst/Respire/blob/5fd8e478730a2df7ded8f9743306304382e1c426/src/Respire/Networking/RespireConnection.Streaming.cs). A 256 KiB exact `MemoryStream` produces eight 32 KiB chunks through `async Task<ReadOnlyMemory<byte>> ReadChunkAsync`; returning a memory value still requires a Task even when the read completes inline. Each upload constructs a reader, its ownership lock and linked cancellation. `CopyStreamPayloadAsync` still uses an unpooled async ValueTask builder; socket-drain suspension can allocate its box. Cancellable `Task.WaitAsync` calls can add wait state when writes remain incomplete. The public benchmark itself returns an async Task, and pooled builders can miss on a continuation thread. Derived delayed streams additionally use background fills and `Task.Delay`. The final-wait builder pools one suspended box; it does not eliminate these other owners. These source sites causally explain why total upload allocation remains nonzero and suspension-dependent. Without allocation stacks and suspension/pool-miss counts, they do not attribute the exact mixed launch deltas. #1474 requires those missing causal measurements before choosing a further fix.

The 100-command batch increases are also not fully resolved. Disabled/one-connection reports are 30010/30018/30016, and candidate launch 2 (30018.027) exceeds every control launch. Disabled/two-connection reports drop to 30015/30004/30018, with candidate 30013.660/30003.613; this is not the earlier audit's consistent candidate increase. Enabled/one-connection candidate launches 30015.758/30016.531 exceed every control launch (30013.080–30014.564). Enabled/two-connection launch 2 (30017.453) exceeds every control launch, while launch 1 (30011.107) is below A and above B. Some 32-command launches also increase; all are retained below.

The identical [batch implementation](https://github.com/thomhurst/Respire/blob/5fd8e478730a2df7ded8f9743306304382e1c426/src/Respire/RespireBatch.cs) constructs a batch/list, a `RespirePending<T>` and `Op<TCommand,T>` for every queued increment, and expands list storage. Standalone execution rents send/observation arrays and awaits replies through pooled builders; the fixture wraps it in an async Task. No timed batch call reaches `WaitForFinalFrameWriteAsync`. The unrelated setup upload can affect process/pool state, and process-wide GC counters include background work. Construction explains the large nonzero total but not a particular extra 2–10 B/op. An unchanged call graph is evidence against direct batch allocation from the changed helper, not proof that every difference is noise. #1474 must separate construction, suspension, pool misses and background allocations with causal controls.

PING, error and small/large SET reports vary by at most two bytes in this bracket; raw/public pipeline reports are unchanged. Selection reports zero, with tiny amortized raw diagnostic ratios. Circuit GET reports vary substantially: disabled A/C/B are 14452/15571/13635 and 14538/13603/15526; enabled are 13639/15004/13866 and 14594/15359/14010. Its complete raw launch allocations must be diagnosed alongside upload/batch evidence, not excluded from the public fixture.

### Internal selection regimes

The identical [multiplexer selection source](https://github.com/thomhurst/Respire/blob/5fd8e478730a2df7ded8f9743306304382e1c426/src/Respire/Infrastructure/RespireConnectionMultiplexer.cs#L395) has two actual paths: one connection uses an inlined health check without the round-robin atomic increment; two connections increment `_next` with `Interlocked.Increment`, then select/check a slot. Neither path calls the streamed helper or the opt-in write observation owner. The diagnostic disassembly contains the one-connection health checks and the two-connection `lock xadd`, which explains the connection-profile cost distinction. It does not explain every same-profile launch difference.

Disabled one-connection selection is +17.80%/+18.49%, with candidate interval 2.210–2.230 ns versus A 1.861–1.908 and B 1.848–1.898. Candidate launches are 2.232/2.209 ns versus A 1.852/1.914 and B 1.906/1.841. These are separated results and remain acceptance evidence even though selection source is unchanged. Disabled two-connection A launches differ at 11.628/10.459 ns; candidate is 10.213/10.269 and B 10.287/10.314. Enabled two-connection candidate launches differ at 9.798/8.538 ns versus A 8.517/8.810 and B 8.828/8.834. Enabled one-connection launches remain around 1.65–1.69 ns.

The regimes already exist in raw workload timing, not only in subtracted reports. Enabled/two-connection candidate actual workload means are 13.877/12.444 ns, while overhead actual means are 3.952/3.900 ns. Disabled/two-connection A actual means are 15.651/14.467 ns, while overhead is 3.998/3.995 ns. Disabled/one-connection candidate actual means are 6.233/6.210 ns versus controls 5.841–5.918 ns, with overhead about 3.995–4.017 ns across those launches. The full result uses overhead estimation and outlier handling, so subtracting these two displayed means will not reproduce every result exactly. Overhead alone cannot explain these differences.

The depth-three diagnostic selection bodies have the same instruction sequence after normalizing absolute addresses across A/C/B and both modes, for each connection profile. Their sizes are 1,122/1,069 bytes. Those captures are one body per case, without code/tier capture tied to each of the two timed launches. They cannot establish that the slow launch executed identical machine code, identify a JIT tier/layout cause, or isolate CPU scheduling/frequency. The earlier audit's enabled one-connection candidate regime (10.998/2.677 ns) remains unresolved too; its AMD runner and older fixture differ from this run. No causal claim of a streaming-induced selection slowdown, or a demonstrated harmless JIT regime, is made. #1474 requires launch-specific code evidence and controlled diagnosis; rerunning unchanged samples to select a favorable regime is insufficient.

### Selection launch capture and causal controls (#1480)

The selection investigation adds an opt-in executor wrapper to the existing transport comparison. The public benchmark bodies, connection setup, errors, background work, allocations and transport contracts stay intact. All public paths still run with the same Medium sampling profile. Only selection children receive JIT logging and, on Linux, perf maps. The other public measurements keep their original runtime settings. This is a material evidence-fixture change, not a production selection optimization or evidence that streaming changed selection.

[BenchmarkDotNet 0.15.8's execution loop](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.8/src/BenchmarkDotNet/Running/BenchmarkRunnerClean.cs) attaches `NoOverhead` diagnosers only to the last measured launch. Registering an ordinary diagnoser would silently omit launch 1. `SelectionEvidenceToolchain` instead wraps each selection execution and preserves the existing executor and diagnosers. The wrapper uses the executor's actual launch index. Extra depth-three disassembly executions go into a `disassembly` subdirectory with purpose `disassembly`; they cannot stand in for a measured process.

Each selection child writes its own [JIT output](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/viewing-jit-dumps.md) with method names, tiers, code bytes and alignment boundaries. Linux [perf maps](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/debugging-profiling#export-perf-maps-and-jit-dumps) preserve native entry addresses, sizes and tier symbols for code/layout comparisons. Unique artifact names and before/after measurement records bind these files to the exact library revision, copied fixture revision, bracket phase, case, job, metric mode, PID and launch index. The workflow checks this binding against the full report's case and launch samples. It rejects absent launch 1 evidence, unrelated disassembly, missing code/tier capture, wrong revisions/modes and missing raw workload or overhead samples. Both error-metric matrix legs require this evidence. Each completed leg produces `results/selection-launches.json` with raw samples, process CPU time and scheduling snapshots; the original full reports and logs are also retained.

JIT/perf-map logging is observer work during compilation, including any compilation that occurs after warmup. It can perturb tier timing and process state even though the timed selection body is unchanged. The captures show generated versions, not which version executed every individual invocation. Do not infer a streaming effect, a harmless regime or equivalence from matching normalized disassembly alone. Compare native addresses, code size, tier versions and raw workload/overhead within the same captured launch. The normal public transport comparison remains the acceptance experiment; the separately named diagnostic controls provide causal sensitivity evidence.

The workflow runs a second baseline/candidate/baseline bracket filtered to the **same** selection method and full setup. Four Short jobs each retain two launches and both connection profiles. All four explicitly use unroll factor 16 to preserve the same engine shape. The diagnostic reference and controls share sampling counts, unlike the public Medium job:

| Diagnostic job | Single changed factor | Interpretation boundary |
| --- | --- | --- |
| `selection-reference` | None | Reference for the three diagnostic controls; not a replacement for public results. |
| `selection-tiering-off` | `DOTNET_TieredCompilation=0` | Removes tier transitions and dynamic tiered optimization. A changed regime shows sensitivity to compilation policy; it does not isolate layout from all code-generation changes. |
| `selection-pinned` | Process affinity restricted to one allowed CPU before Linux runtime startup | Tests sensitivity to migration/scheduling with identical runtime policy. The gate verifies the requested singleton CPU for the process and every captured live task at both measurement boundaries. Background transport work still runs and shares that CPU, so a change can include contention. |
| `selection-no-subtraction` | BenchmarkDotNet overhead evaluation disabled | Compares unadjusted workload with the diagnostic reference's raw workload, overhead and retained results. It does not remove selection work or change its connection profile. |

Linux boundary records retain process/thread `stat`, `schedstat`, `sched` and `status`, plus governor/current-frequency files and `/proc/cpuinfo`. They expose runnable wait, context switches, observed CPU placement and effective affinity. Frequency files can be absent on virtual runners; absence is recorded explicitly. Current-frequency and cpuinfo snapshots are not cycle counters, do not measure frequency throughout an iteration and cannot establish a frequency cause. No fixed-frequency intervention is claimed. If tier/affinity/overhead controls and these observations cannot separate code layout, contention and frequency, the remaining cause stays unresolved. A later audit must state that limitation, rather than call the regime noise. Two launches and Short diagnostic samples also do not establish reproducibility across independent runners.

The historical enabled two-connection 9.798/8.538 ns regime remains unresolved: its raw workload means already differ at 13.877/12.444 ns while overhead means are 3.952/3.900 ns. Disabled one-connection +17.80%/+18.49% also remains a measured regression against both controls, with candidate raw workload 6.233/6.210 ns versus control 5.841–5.918 ns and overhead around 3.995–4.017 ns. Thus overhead alone is insufficient in both examples. The historical artifacts cannot retrospectively acquire launch-specific code or scheduling evidence. The new captures and controls are required to evaluate new regimes; they cannot prove the cause of those historical launches.

Local validation of the initial capture fixture at source commit `24d879d1e7fad18471640e9d4f917b6d4a73517a` identifies a concrete confound in [BenchmarkDotNet's engine selection](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.8/src/BenchmarkDotNet/Engines/EngineFactory.cs). With 100 ms iterations, the enabled/tiering-off/two-connection second launch spends 11.868815 ms in its first workload Jitting invocation. The engine calculates fewer than sixteen invocations per iteration, selects unroll factor 1 and disables overhead. The provenance gate rejects this launch; it accepts the sixteen disabled-mode launches. A 10 ms smoke profile also triggers this behavior. Compilation/logging latency can therefore change both generated workload shape and subtraction policy without changing the selection source. This is a diagnosed local fixture effect, not an attribution of historical regressions.

The final diagnostic jobs fix unroll factor 16 explicitly, bypassing that adaptive engine choice while retaining normal pilot/warmup/workload execution. The gate requires that setting and requires overhead in every reference, tiering-off and pinned launch. Public Medium jobs retain their original adaptive policy; unexpected missing overhead remains an evidence failure. Local frequency files are unavailable, and no frequency attribution is made. Eight net8.0 Dry processes preserve both launch identities and metric modes; guarded builds pass on both target frameworks.

The fixed-engine fixture at source commit `355449d76` passes the original provenance gate for all 32 Linux net10.0 diagnostic processes in both modes. Every process has bound JIT output, native maps and scheduling records. Per mode, twelve reference/tiering-off/pinned launches retain actual overhead samples; four no-subtraction launches omit them. That gate verifies only the main-task CPU mask and tiering-off flags. It does not prove that pre-existing runtime tasks share the main task's affinity. This causal control removes the observed adaptive engine confound; it does not resolve the historical selection regimes or establish a frequency cause. Local validation still uses one warmup, three actual iterations and 100 ms iterations, rather than the workflow's acceptance sampling. Evidence and logs are archived outside the checkout at `C:/git/Respire-evidence/issue-1480`.

The corrected Linux launch wraps the managed executable in `taskset` before runtime startup, so newly created tasks inherit the requested singleton mask. The verifier requires usable process and main-task scheduling records at both measurement boundaries, and complete scheduling records for other tasks with a usable final status. Tasks that exit during capture may be absent; this does not establish their affinity throughout the iteration. A missing perf map remains an explicit evidence-gate failure and no longer aborts the benchmark host. Earlier gate successes cannot substitute for this stronger validation.

The source distinction remains unchanged: one connection skips `_next` and its atomic increment; two connections execute `Interlocked.Increment` before selecting a healthy slot. Preserve that distinction when comparing native code, raw workload and overhead. Neither timed path reaches the streamed final-frame helper. #1481 owns the completed comparison's aggregate acceptance and disposition of both modes; #1474, #1214 and #862 remain open. The implementation PR requests `run-transport-benchmarks` and leaves asynchronous results for that later audit.

### Retention disposition and next measurement

The ownership and retention limits above still apply. Each opt-in attempt owns one non-pooled observation with no payload/source history; limits are per attempt, not a global observation count or time cap. Source reuse remains generation-safe. The fixture does not invoke an opt-in attempt owner. All 1,536 full-GC samples retain 5,767,168 bytes of caller arrays; measured groups contain 24 samples per connection/stage. POH and whole-process memory ranges follow below. None measures isolated runtime pool retention, observation bytes or complete reclamation. Three connection owners and runtime/background state further prevent such an inference.

#1474 is the required material diagnostic/implementation follow-up, with #1432 recorded as its closed prerequisite. It must attribute allocation types/stacks, controlled suspension and pool misses, capture each selection launch's code/tiering, and disposition circuit GET. Any resulting fix preserves FIFO, payload ownership, mutation barriers, exceptions, default limits and source generations. Its implementation PR requests `run-transport-benchmarks`; a later pass audits both completed metric modes without waiting or rerunning unchanged failures for green. #1214 stays open until its full acceptance is established, and #862 stays open for conservative retry-policy work.

### Complete case and launch evidence

#### Error metrics disabled

validation-baseline: 24 cases, 24 successful exits, 96 full-GC samples.

validation-candidate: 24 cases, 24 successful exits, 96 full-GC samples.

baseline-a: 24 cases, 48 successful exits, 192 full-GC samples.

candidate: 24 cases, 48 successful exits, 192 full-GC samples.

baseline-b: 24 cases, 48 successful exits, 192 full-GC samples.

Intervals are exported 99.9% intervals in microseconds. Positive deltas mean slower. Allocation is exported B/op. Batch units are whole batches; pipeline units are commands.

| Case / parameters | C/A; C/B | B/A drift | Interval us: A / C / B | B/op: A / C / B |
| --- | ---: | ---: | --- | ---: |
| CircuitStreamedGet / Connections=1 | +0.24%; -0.73% | +0.98% | 268.703326–275.953877 / 269.403907–276.557541 / 271.699965–278.275330 | 14452 / 15571 / 13635 |
| CircuitStreamedGet / Connections=2 | +4.65%; -4.89% | +10.03% | 276.113765–282.687084 / 273.955033–310.848068 / 283.668803–331.198833 | 14538 / 13603 / 15526 |
| ClientPing / Connections=1 | +0.29%; -0.06% | +0.35% | 114.732639–115.965733 / 114.959417–116.410408 / 114.832044–116.668635 | 184 / 184 / 184 |
| ClientPing / Connections=2 | +0.33%; +1.09% | -0.76% | 115.205960–116.933262 / 115.760414–117.140466 / 114.644057–115.737631 | 184 / 184 / 184 |
| ClientServerError / Connections=1 | +0.31%; -0.47% | +0.79% | 137.361911–138.855291 / 137.589296–139.485554 / 138.265283–140.125097 | 952 / 952 / 952 |
| ClientServerError / Connections=2 | +0.14%; -0.28% | +0.42% | 138.090195–140.670462 / 138.664136–140.480554 / 138.883831–141.045146 | 952 / 952 / 952 |
| LargeSet / Connections=1 | +1.46%; +1.48% | -0.02% | 1193.171265–1236.843387 / 1207.516982–1257.908182 / 1193.095527–1236.463059 | 177 / 177 / 177 |
| LargeSet / Connections=2 | -0.59%; +0.07% | -0.66% | 1236.909427–1275.567416 / 1222.069430–1275.540417 / 1229.586563–1266.227950 | 178 / 176 / 176 |
| Pipeline / Connections=1 | -0.23%; -0.16% | -0.07% | 4.319633–4.350789 / 4.308261–4.341959 / 4.318247–4.346043 | 7 / 7 / 7 |
| Pipeline / Connections=2 | -1.15%; -2.71% | +1.60% | 4.805518–4.957688 / 4.735897–4.914764 / 4.894497–5.024519 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | +0.24%; +0.74% | -0.50% | 4.377325–4.401563 / 4.383034–4.416698 / 4.354602–4.380132 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | -2.24%; -2.15% | -0.10% | 4.899915–5.040779 / 4.784632–4.933147 / 4.868439–5.062525 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | +17.80%; +18.49% | -0.58% | 0.001861–0.001908 / 0.002210–0.002230 / 0.001848–0.001898 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | -7.24%; -0.56% | -6.72% | 0.010584–0.011502 / 0.010220–0.010266 / 0.010289–0.010313 | 0 / 0 / 0 |
| SmallSet / Connections=1 | -0.31%; +0.19% | -0.50% | 117.515203–118.537719 / 117.059249–118.250531 / 116.818021–118.051609 | 176 / 176 / 176 |
| SmallSet / Connections=2 | +0.35%; -1.18% | +1.54% | 117.062506–118.522890 / 117.710158–118.703606 / 118.892917–120.331839 | 176 / 176 / 176 |
| StandaloneBatch / Connections=1&count=100 | -0.19%; -0.96% | +0.77% | 284.931022–293.073902 / 285.783740–291.118289 / 288.347345–294.120759 | 30010 / 30018 / 30016 |
| StandaloneBatch / Connections=1&count=32 | -0.28%; -0.00% | -0.27% | 174.628701–176.126283 / 174.285868–175.504218 / 173.909189–175.887852 | 10484 / 10485 / 10482 |
| StandaloneBatch / Connections=2&count=100 | -0.19%; +0.29% | -0.48% | 286.664309–293.310566 / 286.645454–292.232432 / 285.118573–292.084812 | 30015 / 30004 / 30018 |
| StandaloneBatch / Connections=2&count=32 | +0.43%; -0.54% | +0.98% | 174.272474–176.415683 / 175.268241–176.940759 / 176.387475–177.723866 | 10484 / 10486 / 10485 |
| StreamedSetWithDelayedSource / Connections=1 | +1.55%; +5.08% | -3.36% | 9421.861627–9562.486151 / 9505.264710–9772.623245 / 9085.153994–9260.651134 | 12332 / 12364 / 12517 |
| StreamedSetWithDelayedSource / Connections=2 | -3.18%; -2.67% | -0.53% | 9685.301760–9873.282510 / 9396.683703–9539.868220 / 9592.164372–9863.216546 | 12358 / 12437 / 12297 |
| StreamedSetWithInstantSource / Connections=1 | -0.55%; +0.82% | -1.36% | 232.633797–239.036309 / 232.163023–236.914853 / 230.589453–234.676260 | 6437 / 6463 / 6230 |
| StreamedSetWithInstantSource / Connections=2 | +0.42%; +0.99% | -0.56% | 232.835480–238.087304 / 234.031628–238.853908 / 231.654225–236.615962 | 6488 / 6443 / 6452 |

All 15 raw actual workload samples per launch were checked for positive duration, consistent operations and sequential iteration indices. The following means use retained overhead-adjusted results. Launches are not independent repetitions of the bracket experiment. Raw B/op uses each launch's `// GC:` bytes/operations, which is distinct from the exported allocation column.

| Case / parameters | Launch 1, 2 mean us: A / C / B | Raw launch 1, 2 B/op: A / C / B |
| --- | --- | --- |
| CircuitStreamedGet / Connections=1 | 272.263466, 272.393737 / 273.790975, 272.170474 / 276.186443, 273.788851 | 15352.219, 14452.219 / 14352.762, 15570.859 / 14946.508, 13635.375 |
| CircuitStreamedGet / Connections=2 | 275.908487, 282.659566 / 279.220001, 305.583100 / 342.151065, 275.031054 | 15528.406, 14537.996 / 14492.156, 13602.781 / 13815.750, 15525.938 |
| ClientPing / Connections=1 | 115.674550, 115.023822 / 115.867156, 115.502668 / 115.311948, 116.220045 | 184.060, 184.029 / 184.022, 184.052 / 183.544, 184.221 |
| ClientPing / Connections=2 | 116.961874, 115.113615 / 116.885118, 116.015762 / 115.006317, 115.375371 | 184.104, 184.081 / 184.097, 184.112 / 183.597, 184.112 |
| ClientServerError / Connections=1 | 138.575146, 137.608731 / 137.479996, 139.594854 / 138.379868, 140.068750 | 952.193, 951.553 / 952.193, 952.104 / 952.193, 952.104 |
| ClientServerError / Connections=2 | 137.997674, 140.762983 / 140.265325, 138.879364 / 140.563217, 139.365760 | 951.877, 952.059 / 952.059, 952.283 / 952.059, 952.104 |
| LargeSet / Connections=1 | 1202.425297, 1227.589354 / 1229.984734, 1235.440430 / 1198.394767, 1230.071518 | 176.000, 177.078 / 177.078, 177.078 / 176.000, 177.078 |
| LargeSet / Connections=2 | 1260.000030, 1252.476813 / 1220.115040, 1275.582148 / 1235.548001, 1260.266511 | 176.359, 177.797 / 177.797, 176.000 / 177.797, 176.000 |
| Pipeline / Connections=1 | 4.338433, 4.331758 / 4.328322, 4.321898 / 4.334611, 4.329299 | 6.756, 6.760 / 6.760, 6.757 / 6.757, 6.758 |
| Pipeline / Connections=2 | 4.920957, 4.839439 / 4.915193, 4.729049 / 5.023110, 4.895905 | 6.755, 6.763 / 6.758, 6.764 / 6.769, 6.758 |
| PublicPipeline / Connections=1 | 4.380762, 4.397458 / 4.390898, 4.408834 / 4.373763, 4.360479 | 5.758, 5.758 / 5.758, 5.756 / 5.758, 5.758 |
| PublicPipeline / Connections=2 | 4.990006, 4.950688 / 4.865432, 4.852348 / 5.051053, 4.879911 | 5.769, 5.765 / 5.767, 5.767 / 5.766, 5.760 |
| SelectConnection / Connections=1 | 0.001852, 0.001914 / 0.002232, 0.002209 / 0.001906, 0.001841 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 0.011628, 0.010459 / 0.010213, 0.010269 / 0.010287, 0.010314 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 118.542244, 117.510679 / 117.692611, 117.619684 / 117.875295, 116.994335 | 176.044, 176.044 / 176.000, 175.969 / 176.000, 176.067 |
| SmallSet / Connections=2 | 118.447956, 117.137439 / 117.981699, 118.432065 / 118.758315, 120.466441 | 176.180, 175.984 / 176.180, 176.117 / 176.180, 176.225 |
| StandaloneBatch / Connections=1&count=100 | 287.157426, 290.847498 / 287.577205, 289.324824 / 292.679287, 289.788818 | 30013.184, 30010.188 / 30012.457, 30018.027 / 30015.121, 30015.848 |
| StandaloneBatch / Connections=1&count=32 | 175.931205, 174.860692 / 175.012899, 174.768768 / 174.181662, 175.567588 | 10477.346, 10484.287 / 10484.664, 10484.922 / 10481.744, 10482.301 |
| StandaloneBatch / Connections=2&count=100 | 289.802899, 290.171976 / 288.274957, 290.602928 / 288.782027, 288.421358 | 30012.242, 30014.602 / 30013.660, 30003.613 / 30014.988, 30017.902 |
| StandaloneBatch / Connections=2&count=32 | 176.585781, 174.102376 / 176.102555, 176.106584 / 176.964241, 177.153630 | 10486.324, 10483.994 / 10484.615, 10485.826 / 10483.463, 10484.842 |
| StreamedSetWithDelayedSource / Connections=1 | 9464.876167, 9519.471612 / 9561.637423, 9721.772429 / 9145.717321, 9200.087807 | 12208.000, 12331.875 / 12522.250, 12363.500 / 12513.500, 12517.000 |
| StreamedSetWithDelayedSource / Connections=2 | 9779.198800, 9779.385470 / 9470.466134, 9465.917314 / 9846.287964, 9609.092954 | 12609.625, 12358.375 / 12462.250, 12436.750 / 12261.125, 12297.000 |
| StreamedSetWithInstantSource / Connections=1 | 236.609910, 235.060197 / 234.238709, 234.839167 / 233.263392, 231.844687 | 6327.828, 6437.324 / 6501.844, 6462.637 / 6485.004, 6229.949 |
| StreamedSetWithInstantSource / Connections=2 | 235.625503, 235.297281 / 236.990768, 235.894768 / 234.309694, 233.960493 | 6501.441, 6488.363 / 6371.367, 6443.465 / 6469.293, 6452.254 |

Full-GC samples cover the whole process and retain 5,767,168 bytes of caller arrays. They do not isolate transport retention. POH fragmentation is zero in every sample.

| Phase | Connections | Stage | Samples | Managed bytes | POH bytes | Working set bytes | Private bytes |
| --- | ---: | --- | ---: | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 24 | 5913432–5927824 | 8184–8184 | 49549312–49926144 | 134438912–134672384 |
| baseline-a | 1 | connected | 24 | 8679576–8695984 | 24528–24528 | 67850240–68214784 | 261210112–261386240 |
| baseline-a | 1 | after-churn | 24 | 9699192–9723224 | 24528–24528 | 71303168–71823360 | 263507968–280571904 |
| baseline-a | 1 | after-dispose | 24 | 8239216–9099656 | 24528–24528 | 75169792–110256128 | 246517760–407040000 |
| baseline-a | 2 | before-connect | 24 | 5913544–5921792 | 8184–8184 | 49598464–49971200 | 134443008–134483968 |
| baseline-a | 2 | connected | 24 | 10285400–10305016 | 24528–24528 | 69640192–70144000 | 263565312–263827456 |
| baseline-a | 2 | after-churn | 24 | 11315424–11336416 | 24528–24528 | 73875456–74436608 | 283615232–283901952 |
| baseline-a | 2 | after-dispose | 24 | 8919768–10149128 | 24528–24528 | 75288576–110190592 | 280051712–448421888 |
| candidate | 1 | before-connect | 24 | 5913640–5921896 | 8184–8184 | 49557504–49971200 | 134443008–134676480 |
| candidate | 1 | connected | 24 | 8679576–8696224 | 24528–24528 | 67887104–68300800 | 261181440–261382144 |
| candidate | 1 | after-churn | 24 | 9698176–9720968 | 24528–24528 | 71323648–71847936 | 263585792–280551424 |
| candidate | 1 | after-dispose | 24 | 8250616–8822648 | 24528–24528 | 75206656–110645248 | 263192576–448294912 |
| candidate | 2 | before-connect | 24 | 5913544–5921904 | 8184–8184 | 49594368–49979392 | 134438912–134676480 |
| candidate | 2 | connected | 24 | 10288544–10305192 | 24528–24528 | 69623808–70164480 | 263655424–263872512 |
| candidate | 2 | after-churn | 24 | 11318880–11335696 | 24528–24528 | 73875456–74432512 | 283701248–283934720 |
| candidate | 2 | after-dispose | 24 | 8934224–9833136 | 24528–24528 | 75722752–111820800 | 263983104–410558464 |
| baseline-b | 1 | before-connect | 24 | 5913432–5921784 | 8184–8184 | 49598464–49958912 | 134438912–134676480 |
| baseline-b | 1 | connected | 24 | 8675360–8691928 | 24528–24528 | 67784704–68317184 | 261173248–261382144 |
| baseline-b | 1 | after-churn | 24 | 9700720–9725304 | 24528–24528 | 71229440–71815168 | 263610368–280576000 |
| baseline-b | 1 | after-dispose | 24 | 8239472–8843872 | 24528–24528 | 75132928–109953024 | 246370304–398295040 |
| baseline-b | 2 | before-connect | 24 | 5913424–5921792 | 8184–8184 | 49577984–49922048 | 134438912–134582272 |
| baseline-b | 2 | connected | 24 | 10292760–10304936 | 24528–24528 | 69758976–70094848 | 263651328–263843840 |
| baseline-b | 2 | after-churn | 24 | 11315376–11337608 | 24528–24528 | 74014720–74452992 | 283701248–283881472 |
| baseline-b | 2 | after-dispose | 24 | 8903744–9811768 | 24528–24528 | 75145216–110157824 | 263741440–408350720 |

Phase CPU user/system seconds and utilization: baseline-a: 974.07 529.75 156%; candidate: 1000.73 528.71 154%; baseline-b: 1002.91 536.13 154%.

#### Error metrics enabled

validation-baseline: 24 cases, 24 successful exits, 96 full-GC samples.

validation-candidate: 24 cases, 24 successful exits, 96 full-GC samples.

baseline-a: 24 cases, 48 successful exits, 192 full-GC samples.

candidate: 24 cases, 48 successful exits, 192 full-GC samples.

baseline-b: 24 cases, 48 successful exits, 192 full-GC samples.

Intervals are exported 99.9% intervals in microseconds. Positive deltas mean slower. Allocation is exported B/op. Batch units are whole batches; pipeline units are commands.

| Case / parameters | C/A; C/B | B/A drift | Interval us: A / C / B | B/op: A / C / B |
| --- | ---: | ---: | --- | ---: |
| CircuitStreamedGet / Connections=1 | -0.85%; -1.02% | +0.17% | 214.776986–220.580721 / 211.650971–220.002066 / 214.038611–222.079509 | 13639 / 15004 / 13866 |
| CircuitStreamedGet / Connections=2 | +11.95%; +8.08% | +3.58% | 213.104613–221.602317 / 223.319171–263.323056 / 221.916898–228.344707 | 14594 / 15359 / 14010 |
| ClientPing / Connections=1 | +0.17%; +1.75% | -1.55% | 91.171032–92.494906 / 91.359645–92.622865 / 88.865291–91.944950 | 184 / 183 / 184 |
| ClientPing / Connections=2 | -0.49%; -3.01% | +2.60% | 89.748729–91.962126 / 89.606324–91.213347 / 92.303156–94.134131 | 184 / 184 / 184 |
| ClientServerError / Connections=1 | +0.66%; +0.60% | +0.06% | 103.519643–106.642319 / 104.204289–107.340842 / 103.697283–106.595305 | 952 / 951 / 952 |
| ClientServerError / Connections=2 | +1.50%; +0.07% | +1.43% | 104.495831–106.291466 / 105.536868–108.411452 / 105.669642–108.138752 | 951 / 952 / 952 |
| LargeSet / Connections=1 | +0.50%; -0.70% | +1.20% | 997.171421–1016.649581 / 998.323546–1025.481225 / 1005.549756–1032.477953 | 176 / 176 / 177 |
| LargeSet / Connections=2 | +0.23%; -1.07% | +1.31% | 982.571083–1007.376139 / 986.753029–1007.738707 / 992.486120–1023.510811 | 176 / 176 / 176 |
| Pipeline / Connections=1 | +0.30%; +0.33% | -0.03% | 3.423760–3.449027 / 3.434680–3.458638 / 3.415061–3.455423 | 7 / 7 / 7 |
| Pipeline / Connections=2 | +3.39%; -0.92% | +4.36% | 3.435416–3.510781 / 3.492429–3.689575 / 3.550159–3.698871 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | -0.52%; -0.87% | +0.36% | 3.459167–3.487386 / 3.443714–3.466973 / 3.469672–3.501673 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | -2.30%; -0.71% | -1.60% | 3.543675–3.644719 / 3.442794–3.580586 / 3.479989–3.593479 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | -0.76%; +0.62% | -1.37% | 0.001651–0.001688 / 0.001642–0.001671 / 0.001636–0.001657 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | +5.50%; +3.55% | +1.88% | 0.008561–0.008776 / 0.008687–0.009603 / 0.008812–0.008851 | 0 / 0 / 0 |
| SmallSet / Connections=1 | +0.56%; +0.38% | +0.18% | 93.355759–94.932314 / 93.690526–95.646890 / 93.088456–95.531523 | 176 / 176 / 176 |
| SmallSet / Connections=2 | -0.83%; +2.38% | -3.14% | 94.895782–96.134738 / 94.081107–95.360248 / 91.845473–93.195645 | 176 / 176 / 176 |
| StandaloneBatch / Connections=1&count=100 | +0.40%; -0.63% | +1.04% | 209.857273–217.964524 / 209.189540–220.347137 / 212.244644–220.015705 | 30013 / 30017 / 30015 |
| StandaloneBatch / Connections=1&count=32 | -0.23%; +0.86% | -1.08% | 136.550137–139.272683 / 135.918458–139.280355 / 135.041462–137.799800 | 10483 / 10486 / 10483 |
| StandaloneBatch / Connections=2&count=100 | -1.35%; +2.07% | -3.35% | 214.380124–224.084168 / 210.880374–221.662037 / 207.466044–216.310785 | 30013 / 30017 / 30009 |
| StandaloneBatch / Connections=2&count=32 | +1.81%; +1.40% | +0.40% | 133.424938–136.471974 / 136.184873–138.591033 / 133.791966–137.179385 | 10479 / 10485 / 10481 |
| StreamedSetWithDelayedSource / Connections=1 | -1.46%; -0.82% | -0.64% | 9278.838636–9455.323336 / 9047.248840–9413.170022 / 9191.092537–9422.883933 | 12304 / 12431 / 12237 |
| StreamedSetWithDelayedSource / Connections=2 | +0.46%; +2.42% | -1.91% | 9376.549399–9529.306683 / 9434.826137–9557.505547 / 9096.481830–9447.576876 | 12593 / 12277 / 12371 |
| StreamedSetWithInstantSource / Connections=1 | -0.66%; -0.05% | -0.62% | 177.089270–180.136791 / 175.782842–179.072549 / 175.872094–179.154002 | 6434 / 6481 / 6498 |
| StreamedSetWithInstantSource / Connections=2 | -0.82%; -3.73% | +3.02% | 175.285625–180.334896 / 174.469013–178.220845 / 181.226035–185.143316 | 6487 / 6464 / 6519 |

All 15 raw actual workload samples per launch were checked for positive duration, consistent operations and sequential iteration indices. The following means use retained overhead-adjusted results. Launches are not independent repetitions of the bracket experiment. Raw B/op uses each launch's `// GC:` bytes/operations, which is distinct from the exported allocation column.

| Case / parameters | Launch 1, 2 mean us: A / C / B | Raw launch 1, 2 B/op: A / C / B |
| --- | --- | --- |
| CircuitStreamedGet / Connections=1 | 216.605764, 218.751943 / 216.580035, 215.073002 / 218.339874, 217.758187 | 14514.316, 13639.062 / 13696.477, 15004.164 / 13125.613, 13865.609 |
| CircuitStreamedGet / Connections=2 | 217.188728, 217.518203 / 220.570086, 266.072142 / 227.724073, 222.352299 | 13357.887, 14593.910 / 14132.953, 15359.438 / 15480.906, 14010.363 |
| ClientPing / Connections=1 | 91.647735, 92.018204 / 92.038026, 91.944484 / 88.872686, 92.047014 | 183.989, 184.067 / 184.036, 183.364 / 183.593, 184.036 |
| ClientPing / Connections=2 | 89.364146, 92.346709 / 89.744672, 91.074999 / 94.314006, 92.123281 | 184.066, 184.104 / 183.867, 184.081 / 183.746, 184.089 |
| ClientServerError / Connections=1 | 102.983073, 107.178889 / 103.790065, 107.896673 / 103.896078, 106.396510 | 951.823, 952.059 / 949.354, 951.261 / 952.081, 952.097 |
| ClientServerError / Connections=2 | 106.075370, 104.757375 / 106.229533, 107.718787 / 108.267585, 105.540809 | 951.748, 951.354 / 952.118, 952.142 / 952.142, 952.021 |
| LargeSet / Connections=1 | 1008.829054, 1004.991947 / 1019.457714, 1004.347057 / 1012.354494, 1025.673214 | 176.000, 176.000 / 176.000, 176.000 / 176.250, 177.078 |
| LargeSet / Connections=2 | 1004.426099, 985.521123 / 997.852970, 996.638765 / 1020.224876, 996.587148 | 176.000, 176.250 / 176.000, 176.000 / 176.000, 176.000 |
| Pipeline / Connections=1 | 3.433118, 3.439450 / 3.454664, 3.438654 / 3.456667, 3.413817 | 6.756, 6.758 / 6.761, 6.753 / 6.759, 6.754 |
| Pipeline / Connections=2 | 3.447343, 3.498854 / 3.700064, 3.481939 / 3.685851, 3.563178 | 6.743, 6.739 / 6.762, 6.734 / 6.757, 6.719 |
| PublicPipeline / Connections=1 | 3.474098, 3.472455 / 3.458156, 3.452530 / 3.470867, 3.498504 | 5.757, 5.758 / 5.757, 5.757 / 5.756, 5.757 |
| PublicPipeline / Connections=2 | 3.592018, 3.596376 / 3.549817, 3.470840 / 3.548682, 3.524786 | 5.754, 5.752 / 5.742, 5.655 / 5.748, 5.752 |
| SelectConnection / Connections=1 | 0.001690, 0.001648 / 0.001651, 0.001662 / 0.001647, 0.001645 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 0.008517, 0.008810 / 0.009798, 0.008538 / 0.008828, 0.008834 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 94.485622, 93.802451 / 93.423724, 95.913692 / 95.554624, 93.065355 | 175.818, 176.015 / 175.977, 176.060 / 176.025, 175.747 |
| SmallSet / Connections=2 | 95.457527, 95.569145 / 94.862097, 94.579258 / 92.341442, 92.699676 | 176.094, 176.097 / 176.008, 176.090 / 176.022, 175.784 |
| StandaloneBatch / Connections=1&count=100 | 211.888299, 215.933497 / 217.019568, 212.356306 / 219.001317, 213.053950 | 30014.004, 30013.398 / 30015.758, 30016.531 / 30013.080, 30014.564 |
| StandaloneBatch / Connections=1&count=32 | 136.269826, 139.552994 / 139.193267, 136.005546 / 136.471575, 136.366048 | 10482.820, 10483.381 / 10485.848, 10485.605 / 10476.846, 10482.820 |
| StandaloneBatch / Connections=2&count=100 | 217.480785, 220.983507 / 215.896890, 216.645521 / 209.510569, 214.266260 | 30011.545, 30012.795 / 30011.107, 30017.453 / 30006.008, 30009.246 |
| StandaloneBatch / Connections=2&count=32 | 134.937607, 134.958582 / 138.426707, 136.349199 / 136.531693, 134.439658 | 10481.766, 10478.939 / 10480.781, 10485.270 / 10481.682, 10481.436 |
| StreamedSetWithDelayedSource / Connections=1 | 9365.726822, 9368.435150 / 9348.800407, 9111.618455 / 9195.323002, 9418.653468 | 12498.000, 12304.500 / 12333.625, 12430.625 / 12455.500, 12237.375 |
| StreamedSetWithDelayedSource / Connections=2 | 9444.173968, 9461.682114 / 9484.611243, 9507.720442 / 9482.850540, 9028.774138 | 12467.750, 12592.875 / 12346.125, 12277.375 / 12374.125, 12371.000 |
| StreamedSetWithInstantSource / Connections=1 | 178.913446, 178.312615 / 177.365562, 177.489829 / 178.410798, 176.675149 | 6428.850, 6434.176 / 6508.283, 6480.641 / 6508.834, 6497.826 |
| StreamedSetWithInstantSource / Connections=2 | 179.261195, 176.359325 / 176.477853, 176.202510 / 182.788095, 183.554817 | 6502.273, 6486.949 / 6465.521, 6464.102 / 6510.791, 6518.684 |

Full-GC samples cover the whole process and retain 5,767,168 bytes of caller arrays. They do not isolate transport retention. POH fragmentation is zero in every sample.

| Phase | Connections | Stage | Samples | Managed bytes | POH bytes | Working set bytes | Private bytes |
| --- | ---: | --- | ---: | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 24 | 5913488–5927896 | 8184–8184 | 49537024–49942528 | 134443008–134676480 |
| baseline-a | 1 | connected | 24 | 8676712–8693288 | 24528–24528 | 67846144–68345856 | 261210112–261378048 |
| baseline-a | 1 | after-churn | 24 | 9701288–9720624 | 24528–24528 | 71233536–71811072 | 263512064–280596480 |
| baseline-a | 1 | after-dispose | 24 | 8251416–8820400 | 24528–24528 | 75362304–169742336 | 247877632–404480000 |
| baseline-a | 2 | before-connect | 24 | 5913496–5928008 | 8184–8184 | 49651712–49893376 | 134447104–134676480 |
| baseline-a | 2 | connected | 24 | 10285936–10304576 | 24528–24528 | 69754880–70053888 | 263630848–263831552 |
| baseline-a | 2 | after-churn | 24 | 11318464–11334856 | 24528–24528 | 73998336–74375168 | 266784768–283922432 |
| baseline-a | 2 | after-dispose | 24 | 8973784–10246456 | 24528–24528 | 76406784–169291776 | 267210752–443617280 |
| candidate | 1 | before-connect | 24 | 5913600–5928112 | 8184–8184 | 49545216–49942528 | 134443008–134483968 |
| candidate | 1 | connected | 24 | 8680072–8696704 | 24528–24528 | 67706880–68333568 | 261173248–261386240 |
| candidate | 1 | after-churn | 24 | 9702576–9716968 | 24528–24528 | 71151616–71790592 | 263495680–280592384 |
| candidate | 1 | after-dispose | 24 | 8235280–9154776 | 24528–24528 | 75247616–168988672 | 247922688–458940416 |
| candidate | 2 | before-connect | 24 | 5913624–5928120 | 8184–8184 | 49430528–49868800 | 134447104–134483968 |
| candidate | 2 | connected | 24 | 10289008–10305480 | 24528–24528 | 69611520–70033408 | 263643136–263884800 |
| candidate | 2 | after-churn | 24 | 11314112–11332128 | 24528–24528 | 73797632–74276864 | 266833920–283959296 |
| candidate | 2 | after-dispose | 24 | 8910968–10143392 | 24528–24528 | 76058624–170033152 | 265289728–406728704 |
| baseline-b | 1 | before-connect | 24 | 5921712–5927872 | 8184–8184 | 49524736–49897472 | 133828608–134483968 |
| baseline-b | 1 | connected | 24 | 8679960–8696480 | 24528–24528 | 67817472–68308992 | 260620288–261394432 |
| baseline-b | 1 | after-churn | 24 | 9644584–9718456 | 24528–24528 | 71237632–71831552 | 263380992–280592384 |
| baseline-b | 1 | after-dispose | 24 | 8246032–8842984 | 24528–24528 | 75157504–169955328 | 247808000–421814272 |
| baseline-b | 2 | before-connect | 24 | 5913536–5927976 | 8184–8184 | 49635328–49901568 | 134447104–134676480 |
| baseline-b | 2 | connected | 24 | 10293144–10308744 | 24528–24528 | 69664768–72011776 | 263630848–263880704 |
| baseline-b | 2 | after-churn | 24 | 11312672–11358520 | 24528–24528 | 73859072–76304384 | 266784768–283914240 |
| baseline-b | 2 | after-dispose | 24 | 9013768–10106528 | 24528–24528 | 76152832–150667264 | 265383936–406212608 |

Phase CPU user/system seconds and utilization: baseline-a: 1147.03 576.75 163%; candidate: 1065.48 545.56 160%; baseline-b: 1074.94 537.36 163%.

## Exact MemoryStream fill-task diagnostic: 2026-10-12

Child [#1477](https://github.com/thomhurst/Respire/issues/1477) isolates one allocation site from [#1474](https://github.com/thomhurst/Respire/issues/1474): the completed `Task<ReadOnlyMemory<byte>>` returned by each exact `MemoryStream` fill. A 256 KiB source fills eight 32 KiB chunks. The reader now reuses its successful completed task while the buffer and slice length remain the same. A shorter tail gets a separate result. Failure and cancellation retain their own tasks. Derived streams still use the background fill path, because an overridden `ReadAsync` can block synchronously.

`ExactMemoryStreamReusesCompletedFillTask` measures the actual reader after its first fill, excluding reader construction, buffer rental, source allocation and the first task. The warmed, no-inline boundary runs under an unkeyed `NotInParallel` test inside `AllocationMeasurement.WithoutConcurrentGc`. The task-identity test also verifies different chunk contents, a shorter tail and consumed-prefix state. Error tests cover early EOF, cancellation before the next read, and the original disposed-source exception.

On Windows x64, the unchanged read implementation at `becea358c6762202b5929907112772ebb471cec2` allocates **560 bytes for seven subsequent fills** on .NET 8.0.31 and .NET 10.0.12. Both the zero-byte assertion and the task-identity assertion fail before the change. The changed reader allocates **zero bytes in that same boundary**. Its causal positive control recreates a completed task for each subsequent fill and allocates **560 bytes**, identifying an 80-byte task per fill in these local runs. This is a test-only diagnostic, not a public timing launch.

This does not establish a zero-allocation upload or attribute a historical process-wide launch delta. The first fill task and reader still allocate. Linked cancellation, suspended payload copying, cancellable write waits, fixture tasks and pooled-builder misses remain separate sites. Standalone batch allocation, selection code/tiering and scheduling, delayed uploads, and circuit-enabled streamed GET still require their own evidence. The normal public timing fixture retains every path; a new pinned comparison must audit both metric modes, all rows, raw launches, control drift and the retention limits above.

The remaining native children are [residual stream/GET diagnosis #1478](https://github.com/thomhurst/Respire/issues/1478), [batch diagnosis #1479](https://github.com/thomhurst/Respire/issues/1479), [selection diagnosis #1480](https://github.com/thomhurst/Respire/issues/1480), and [final aggregate audit #1481](https://github.com/thomhurst/Respire/issues/1481). #1474, #1214 and #862 remain open. Whole-process memory with caller arrays still cannot establish isolated retention or a global observation cap. Large non-streamed SET copying/gather-send remains #976.

## Residual stream suspension diagnostic: 2026-10-12

Child [#1478](https://github.com/thomhurst/Respire/issues/1478) adds separate EventPipe allocation launches after the exact `MemoryStream` fill-task change. The measured source is `6412839d802d87d4e0ef48748832ac2b3db9f156`, based on main `a3701c6cd`. This is a diagnostic fixture change; no production transport code or public BenchmarkDotNet timing body changes. The diagnostic invokes the existing public instant upload, delayed upload and circuit-enabled streamed GET bodies with their original setup. An internal client accessor supports a separate pooled `ValueTask` fixture control.

Each invocation records the full source revision, case, connection profile, metric mode, launch index, PID, runtime, OS, assembly SHA-256 hashes and control observations in `metadata.json`. `allocations.nettrace` retains the raw allocation ticks, stacks and rundown. `allocations.jsonl` binds the metadata to control boundaries, sampled allocation types and stacks, including PID and thread ID. Conversion rejects missing boundary pairs, duplicate boundaries, absent types/stacks, missing allocation samples and dropped events. Existing evidence directories are rejected rather than overwritten. The capture script rebuilds clean measured source through the unchanged agent guard and checks that its revision and source remain unchanged during the build.

The controls distinguish these owners:

- **Exact reader:** 1,024 independent readers each fill eight 32 KiB chunks from one reusable 256 KiB exact `MemoryStream`. Every reader requires one successful completed task identity across all eight fills. Construction, the first task and ownership lock remain inside this diagnostic boundary; this is not the zero-byte subsequent-fill test from #1477.
- **Derived reader:** the delayed case separately exercises actual background reader fills and records incomplete fills. Its `ReadAsync` awaits the same one-millisecond delay as the public fixture. Background launch, delay, fill builder and task conversion remain distinct from completed inline fills.
- **Linked cancellation:** 1,024 two-token linked sources isolate creation and disposal. This control does not include the default upload deadline's timer or three-token linked source; those remain visible in actual upload stacks. It cannot quantify the whole default cancellation owner.
- **Public Task versus pooled fixture:** both controls execute 128 sequential public operations. The latter uses a diagnostic pooled builder around the same public API and reusable source/read buffer. Different process state, wrapper result shape and caller arrays prevent treating the process-byte difference as an exact fixture-Task cost.
- **Held payload write:** a diagnostic transport skips the SET header, then holds completion of the real first payload socket write. The controller requires the whole upload to remain incomplete before release. This exposes payload-copy and cancellable write-wait allocation stacks. The gate adds its own wait and cancellation state; it does not count every internal suspension or reproduce a stalled production socket's latency.
- **Held GET receive:** a separate circuit-enabled client holds real received bytes before parser handoff. It verifies an incomplete GET/read operation and the complete 256 KiB payload after release. This boundary includes stream acquisition, circuit lifetime and payload reads.
- **Write wait:** 1,024 incomplete `Task.WaitAsync` calls with `CancellationToken.None` require the original task identity; the cancellable control requires a separate wait owner. Both complete normally. Controller task creation remains separately named in stacks.
- **Pooled final wait:** 1,024 sequential actual `WaitForFinalFrameWriteAsync` owners are compared with 1,024 simultaneous incomplete owners. Allocation samples of its pooled state-machine box demonstrate demand misses. They do not count all misses, distinguish every thread-cache miss or set a new production pool/operation limit.
- **Idle and positive controls:** before/after idle windows expose collector/background activity. A named worker creates 128 escaping 64 KiB arrays so process-wide allocation capture has a known positive control outside transport work.

Reproduce against an isolated Redis with `REDIS_HOST` and `REDIS_PORT` set:

```powershell
& scripts/Invoke-TransportStreamDiagnostics.ps1 -Framework net10.0 -Iterations 128 -OutputDirectory C:/git/Respire-evidence/issue-1478/net10
& scripts/Invoke-TransportStreamDiagnostics.ps1 -Framework net8.0 -Iterations 128 -OutputDirectory C:/git/Respire-evidence/issue-1478/net8
pwsh scripts/Test-TransportBatchDiagnostics.ps1 -Kind stream
```

Each framework's default matrix has 24 separate processes: three cases, two connection profiles, two metric modes and two launches. These are diagnostic launch identities, not the historical public timing launch identities. Process allocation deltas include controller, collector and background work. Allocation ticks sample objects; their allocation interval includes allocations of other types since the preceding tick. Neither interval bytes nor sample counts are exact bytes per allocating type. No zero-allocation upload, isolated retained-byte total or performance equivalence follows from these controls.

### Captured allocation evidence

All **48 diagnostic processes** complete on Windows x64 (`Microsoft Windows 10.0.26200`), with .NET 8.0.31 and 10.0.12 and an isolated Docker Redis 8.10. The two framework captures can overlap on that server; their durations are not performance measurements. Every case/profile/mode has both launch identities, and every public, pooled-fixture and held-transport control records 128 incomplete operations. Every delayed reader control records 1,024 incomplete fills. The exact-reader controls verify the same successful task across all eight fills on every reader. The two write-wait controls and both final-wait controls each verify 1,024 incomplete owners per process.

Trace conversion finds **5,917 allocation samples**: 2,973 on net8.0 and 2,944 on net10.0. No events are lost and every sample has a type. Two net8.0 samples and one net10.0 sample lack stacks; they cannot supply stack attribution. The external `summary.json` binds every launch's metadata and raw trace to SHA-256 hashes. The following counts classify the allocated object type across all diagnostic controls, not an unrelated ancestor in its stack and not exact per-operation allocations:

| Sampled owner | net8.0 samples | net10.0 samples | Causal distinction |
| --- | ---: | ---: | --- |
| Linked cancellation sources | 64 | 72 | Named creation control and actual upload/GET stacks; gate sources remain separate |
| `CopyStreamPayloadAsync` async boxes | 15 | 5 | Public, pooled-fixture and held-payload controls; not completed fill tasks |
| `CancellationPromise` wait owners | 99 | 104 | Cancellable waits and actual transport controls; no samples of this type in the non-cancellable wait control |
| Derived fill, unwrap and delay owners | 108 | 111 | Actual reader and delayed source controls retain background fill costs |
| `WaitForFinalFrameWriteAsync` pooled boxes | 28 | 32 | Demand control has 27/32; net8.0 sequential control also samples one miss |
| Public fixture async Task boxes | 6 | 3 | Samples belong to the public Task control, separate from the pooled fixture |
| GET pending/circuit owners and read boxes | 223 | 222 | Public, pooled-fixture and held-receive controls; separate GET lifetime/read costs |

The payload-copy samples carry builder allocation stacks at `AsyncValueTaskMethodBuilder` and `CopyStreamPayloadAsync.MoveNext`. The final-wait samples identify its `PoolingAsyncValueTaskMethodBuilder` state-machine box. Overlapping owners force pool demand; the sequential net8.0 sample also shows why a same-thread warmed pool-hit test cannot promise no misses across arbitrary continuation threads. GET samples include `BulkStreamPendingResponseSource`, `CircuitStreamCompletion`, `PipeReaderStream.ReadAsyncInternal`, `ProgressTrackingStream.RecordProgressAsync` and `CircuitCompletionStream.ReadAsync`, with their allocation stacks. These observations identify residual owners after fill-task reuse. They do not decompose an entire upload/GET total or explain a particular historical mixed launch. Absence of a sampled type in a control is not proof of zero allocation there.

The diagnostic and library projects build on net8.0 and net10.0 under the unchanged guard. The capture-provenance regression passes all eight scenarios for each of the stream and existing batch scripts. The full `Respire.Tests` build exceeds the unchanged 2,048 MB process-tree guard (exit 137: 2,055 MB during net8.0 compilation and 2,066 MB for a net10.0-only build). A temporary external harness instead links the unchanged `StreamedSetTests`, `StreamedSetAllocationTests`, `CommandWriteOutcomeTests`, `FakeRespServer`, `TestTlsCertificate` and `AllocationMeasurement` sources against the actual library and the repository's TUnit 1.74.0 version. Its guarded Release builds succeed on both frameworks; **106/106 tests pass on each**, including the no-GC allocation tests and causal positive controls. This focused harness is not a full-suite pass. Website build also passes. Logs, harness inputs/results and raw diagnostics remain outside the checkout at `C:/git/Respire-evidence/issue-1478` (build logs use `C:/git/issue-1478-*.log`). They are local evidence, not uploaded historical-run artifacts.

### Historical delayed-upload and GET disposition

The pinned final-frame comparison above remains the public acceptance evidence. Delayed uploads remain mixed in both metric modes and connection profiles. Disabled one-connection latency is slower against B only; disabled two-connection latency is faster against both controls. Enabled one-connection candidate allocation launch 2 exceeds both controls' launch 2; disabled two-connection candidate allocations exceed both B launches. Reader offload, delay tasks, cancellation waits and payload-copy suspension can explain nonzero totals, but cannot assign those historical launch differences without matching traces.

Circuit-enabled GET also remains required acceptance scope. In the enabled two-connection case, candidate launch means remain **220.570/266.072 us**, compared with A **217.189/217.518 us** and B **227.724/222.352 us**. Candidate aggregate interval **223.319–263.323 us** is separated and slower against A's **213.105–221.602 us**, while overlapping B's **221.917–228.345 us**. Overlap with B does not dispose of the separation against A. GET does not execute the changed SET wait during its timed region; setup warms SET. New allocation diagnostics cannot retroactively establish the old launch's scheduling, tiering, code layout or pool state. That regime remains unresolved rather than classified as harmless noise or attributed to completed fill tasks.

No focused defect or historical regression cause is established by these sampled owners, so no transport behavior is changed. Derived-stream offload, source callbacks, original exceptions, caller ownership, FIFO, mutation barriers, source generations, defaults and per-attempt retention limits retain their existing contracts. These separate allocation diagnostics do not materially change the normal public timing fixture and do not request another unchanged public comparison. The material reader change already requested its pinned comparison in #1483. Final aggregate acceptance belongs to [#1481](https://github.com/thomhurst/Respire/issues/1481) under #1474; #1214 and #862 remain open.

## Evidence audit: 2026-10-09

The comparison, disposition and tables below are a point-in-time audit of the pinned run. A later audit can supersede this dated evidence without replacing the ownership and retention guidance above.

### Pinned comparison after streaming pooling

[Run 37977384030](https://github.com/thomhurst/Respire/actions/runs/37977384030) measures the material streaming state-machine pooling change from [PR #1374](https://github.com/thomhurst/Respire/pull/1374). Baseline A and B both use `780041a82086e48bf73cde8c66cccbe14b98415b`. The measured candidate is immutable PR merge `5568c1f41bd1b1fed21a891b11d09b80edbd8793`, with PR head `408bd7b09df539c32d2b04ba0627649c3a6367f1`. The workflow verifies both merge parents. Its run metadata head is the PR head; it is not the measured merge revision.

The two artifacts are `transport-acceptance-comparison-disabled` (ID 11642222267) and `transport-acceptance-comparison-enabled` (ID 11642735371). Each metric mode brackets A/candidate/B on its own runner. Both use Ubuntu 24.04 Linux x64, AMD EPYC 7763 with four logical/two physical cores, SDK 10.0.401, runtime 10.0.12, BenchmarkDotNet 0.15.8 and digest-pinned Redis 8.10. Error-metric mode selection and candidate instrument publication are present in every phase. The two modes are separate experiments; their absolute values do not identify an enabled-versus-disabled metric cost.

Each mode has 22 cases, two Dry validation phases and three measured phases. Every measured case has two launches, 15 actual workload iterations per launch and 24–30 retained overhead-adjusted result samples after outlier handling. All 88 validation launches and 264 measured launches exit successfully across both modes. All 3,960 raw actual workload samples and the retained result samples were checked for phase, case, launch and operation count. Each Dry phase has 88 full-GC retention samples; each measured phase has 176, with 22 per connection/stage group. High-priority setup fails with permission denied in every phase. Successful workflow completion proves that measurements finished; it does not establish performance acceptance.

The fixture uses public PING, pipelined PING, small SET, a 5 MiB array-backed SET, streamed 256 KiB SET with instant/delayed sources, server errors and 32/100-command standalone batches. Internal selection and raw pipeline rows are also included. Batch latency/allocation is per whole batch, pipeline rows are per command, and other command rows are per operation. The large array SET exercises the gathered sender introduced by #1217, which is already present in both controls. The gathered command, gathered sender, write-outcome owner and streaming implementations have no diff between the measured candidate merge and audited main `622972d400b96d111b18272ba011fd3edb6958f4`. This includes current-main gathered-path evidence without claiming a benchmark of every later main change, TLS, Windows or net8.0. The existing gathered outcome test covers both socket and copying transports; the performance fixture measures ordinary Linux sockets.

### Audit disposition

**The evidence audit is complete; aggregate transport performance acceptance remains unmet.** Issues [#1214](https://github.com/thomhurst/Respire/issues/1214) and [#862](https://github.com/thomhurst/Respire/issues/862) remain open. No unchanged benchmark was rerun.

No public-command row has a slower 99.9% interval separated from both controls. All public-command rows overlap both controls except these: disabled one-connection server error, public pipeline and small SET, and disabled two-connection batches of 32/100 are faster than A with separated intervals while overlapping B; enabled one-connection delayed streaming is faster than both. Overlap does not prove equivalence or zero cost. Enabled internal raw pipeline with one connection is +1.16% versus A with separated slower intervals (6.679107–6.731860 us versus 6.601205–6.655855 us), but overlaps B (6.652778–6.699057 us). The entire per-row interval/delta audit follows below.

Enabled internal one-connection selection is slower with separated intervals against both controls: candidate 3.737–9.937 ns versus A 2.651–2.669 and B 2.641–2.654 ns, +157.05%/+158.27%. Candidate launch means are 10.998 and 2.677 ns, versus A 2.663/2.657 and B 2.655/2.640 ns, with 62.06% aggregate CV. This is a distinct process regime, not reliable evidence of an effect caused by streaming pooling. The unchanged selection source and stable other candidate launch do not make the slow launch disappear. The artifact retains depth-three disassembly for investigation. Do not use this internal row to claim a public-command regression or rerun it unchanged for a favorable regime.

Delayed streaming allocation decreases against both controls in every raw candidate launch, in both connection profiles and both metric modes. Exported candidate deltas in disabled mode are -311/-252 B/op (one connection) and -379/-393 (two); enabled deltas are -183/-302 and -251/-263. This supports the material pooling improvement. It does not measure an isolated allocator or reproduce the initiating-thread diagnostic boundary from #1372.

Instant streaming allocation is less conclusive. Disabled one-connection allocation is 6520/6472/6511 B/op (A/candidate/B), and both candidate launches are below every control launch. Disabled two-connection allocation is 6476/6491/6493: candidate is +15 B/op versus A and -2 versus B, with raw candidate launches 6322.484/6491.371 versus A 6482.887/6476.074 and B 6512.391/6493.004. Enabled one-connection allocation is 6518/6455/6496, with both candidate launches below controls. Enabled two-connection allocation is 6519/6483/6504, but candidate launch 1 (6515.953) exceeds both B launches (6502.082/6503.703) and one A launch (6500.883). The exported allocation column is not the mean of these raw launch ratios; audit both rather than averaging them into a favorable claim.

Selection remains zero in integer allocation reports; tiny raw baseline ratios arise from diagnostic bytes amortized over many operations. PING, raw/public pipeline and server-error allocation reports are unchanged. Small SET reports differ by at most one byte with overlapping raw launch ranges. Large SET/gathered allocation reports are 321/322/317 and 316/320/315 in disabled mode, and 323/319/321 and 321/321/316 in enabled mode. All four large-SET raw candidate ranges overlap a control range; no consistent candidate-only increase or zero-cost proof follows. Disabled 100-command/two-connection batch allocation is 30043/30053/30048, and both raw candidate launches exceed every matching control launch (30051.480/30052.898 versus 30042.762–30047.848). Other batch allocation variation and all rows are retained below. Pooling does not directly change batch code; these samples cannot supply causal attribution or dismiss the increase.

The old foundation comparison (run 37766977918) used a different baseline, fixture and pre-gathered implementation. Its default instant-stream allocation increase cannot be declared restored by subtracting totals from these separate runs. The new results do not establish a consistent allocation contract for every public path or rule out unrelated changes in the comparison's baseline/candidate boundary. Remaining acceptance requires a material diagnosis/implementation or fixture change that resolves the mixed instant-stream/batch allocation evidence and the internal selection regime, followed by an appropriately pinned comparison. Existing successful rows need no unchanged rerun for green.

All retention samples keep the 5 MiB caller payload and two 256 KiB source arrays (5,767,168 bytes) referenced, including after disposal. POH is 8,184 bytes before connect and 24,528 after connect/churn/dispose, with zero POH fragmentation. Managed/process ranges overlap controls in some groups and differ in others; none isolates transport or opt-in observation bytes. Global setup also owns both a public client and an internal multiplexer, so the connection parameter is not a count of all process resources. The ordinary fixture never calls an opt-in attempt owner. Whole-process GC, working-set/private memory and process CPU include unrelated runtime/driver state. They establish neither complete reclamation nor a finite global observation count/time bound. The source ownership limits above are the retention disposition; no isolated byte measurement is claimed.

### Complete case and launch evidence

The tables preserve every applicable allocation row and latency interval, control drift and both launches. Raw artifacts additionally contain each iteration, native GC diagnostic, phase logs and disassembly.

#### Error metrics disabled

validation-baseline: 22 cases, 22 successful exits, 88 retention samples; validation-candidate: 22 cases, 22 successful exits, 88 retention samples; baseline-a: 22 cases, 44 successful exits, 176 retention samples; candidate: 22 cases, 44 successful exits, 176 retention samples; baseline-b: 22 cases, 44 successful exits, 176 retention samples.

All latency intervals below are exported 99.9% intervals in microseconds. Positive deltas mean slower. Allocation is exported B/op. A and B are bracket controls.

| Case / parameters | C/A; C/B | B/A drift | Interval us: A / C / B | B/op: A / C / B |
| --- | ---: | ---: | --- | ---: |
| ClientPing / Connections=1 | -0.30%; -0.07% | -0.22% | 192.667913–194.274136 / 192.128895–193.663437 / 192.419261–193.654617 | 184 / 184 / 184 |
| ClientPing / Connections=2 | -0.89%; -0.64% | -0.25% | 193.580637–194.916093 / 190.659566–194.373158 / 193.039495–194.487089 | 184 / 184 / 184 |
| ClientServerError / Connections=1 | -1.76%; -0.55% | -1.22% | 225.038128–227.134933 / 220.792434–223.429990 / 221.328512–225.339048 | 952 / 952 / 952 |
| ClientServerError / Connections=2 | -0.57%; +0.34% | -0.90% | 224.252929–227.287223 / 222.928630–226.038644 / 222.333479–225.132950 | 952 / 952 / 952 |
| LargeSet / Connections=1 | -2.24%; +0.51% | -2.74% | 1853.179733–1985.055965 / 1821.236121–1931.022800 / 1821.555809–1911.493702 | 321 / 322 / 317 |
| LargeSet / Connections=2 | -0.23%; -0.91% | +0.68% | 1809.059282–1891.641413 / 1800.207692–1891.892447 / 1811.821826–1914.107292 | 316 / 320 / 315 |
| Pipeline / Connections=1 | +0.08%; +0.14% | -0.06% | 6.804245–6.849571 / 6.787546–6.877619 / 6.793979–6.851851 | 7 / 7 / 7 |
| Pipeline / Connections=2 | -1.13%; +0.05% | -1.18% | 7.770471–7.883600 / 7.695574–7.781433 / 7.689130–7.780830 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | -1.15%; -0.18% | -0.97% | 6.843205–6.907070 / 6.765879–6.826455 / 6.783080–6.833295 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | -0.64%; -0.35% | -0.29% | 7.780005–7.896789 / 7.706764–7.869216 / 7.767083–7.863753 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | +1.79%; +2.31% | -0.51% | 0.002648–0.002685 / 0.002656–0.002772 / 0.002636–0.002669 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | +0.22%; +0.10% | +0.11% | 0.006379–0.006430 / 0.006397–0.006441 / 0.006402–0.006422 | 0 / 0 / 0 |
| SmallSet / Connections=1 | -0.96%; -0.13% | -0.83% | 198.673739–200.050526 / 196.815458–198.079811 / 197.122485–198.293574 | 176 / 176 / 175 |
| SmallSet / Connections=2 | +0.02%; -0.25% | +0.28% | 197.053677–199.228834 / 197.579243–198.786001 / 198.045851–199.328053 | 176 / 176 / 176 |
| StandaloneBatch / Connections=1&count=100 | -0.78%; -0.26% | -0.52% | 357.741186–361.847336 / 355.303358–358.654850 / 356.525773–359.301342 | 30047 / 30044 / 30048 |
| StandaloneBatch / Connections=1&count=32 | +0.44%; +0.12% | +0.32% | 256.256735–257.545931 / 257.329300–258.732467 / 257.140230–258.281569 | 10525 / 10524 / 10524 |
| StandaloneBatch / Connections=2&count=100 | -1.73%; +0.02% | -1.76% | 360.809528–365.843320 / 354.788451–359.267979 / 355.433890–358.459060 | 30043 / 30053 / 30048 |
| StandaloneBatch / Connections=2&count=32 | -1.75%; -0.15% | -1.60% | 261.362990–263.169834 / 256.834592–258.505028 / 257.279029–258.836755 | 10524 / 10521 / 10525 |
| StreamedSetWithDelayedSource / Connections=1 | -0.80%; +2.06% | -2.79% | 10100.285207–10268.846747 / 10034.542269–10172.502822 / 9741.686184–10058.252527 | 12738 / 12427 / 12679 |
| StreamedSetWithDelayedSource / Connections=2 | -0.56%; -1.11% | +0.55% | 9355.542918–9958.906405 / 9343.277166–9862.484025 / 9384.854222–10035.504188 | 12776 / 12397 / 12790 |
| StreamedSetWithInstantSource / Connections=1 | -0.07%; +2.10% | -2.13% | 367.487590–380.546167 / 368.873836–378.611021 / 361.302970–370.827228 | 6520 / 6472 / 6511 |
| StreamedSetWithInstantSource / Connections=2 | -1.57%; -2.49% | +0.95% | 366.395005–379.553863 / 362.704230–371.553851 / 369.649095–383.367845 | 6476 / 6491 / 6493 |

Every raw actual workload sample and retained result sample was checked for its case, phase, launch and operation count. Each row has two launches and 30 actual workload samples. The table reports each launch mean from overhead-adjusted workload results and each raw `// GC:` bytes/operations ratio; these process launches are not independent repetitions of the whole bracket experiment.

| Case / parameters | Launch 1, 2 mean us: A / C / B | Raw launch 1, 2 B/op: A / C / B |
| --- | --- | --- |
| ClientPing / Connections=1 | 193.449929, 193.492120 / 193.114221, 192.678111 / 193.047093, 193.026785 | 184.090, 184.068 / 184.059, 184.090 / 184.090, 183.902 |
| ClientPing / Connections=2 | 194.343610, 194.153121 / 192.536944, 192.495780 / 193.910583, 193.616002 | 183.041, 184.119 / 184.072, 184.135 / 184.135, 184.104 |
| ClientServerError / Connections=1 | 226.306445, 225.881277 / 222.182146, 222.049736 / 222.508509, 224.104033 | 952.133, 951.930 / 950.641, 952.297 / 952.010, 952.203 |
| ClientServerError / Connections=2 | 224.991860, 226.548292 / 225.011465, 223.955809 / 223.646661, 223.819769 | 952.207, 952.193 / 951.209, 952.178 / 952.178, 952.324 |
| LargeSet / Connections=1 | 1892.001147, 1946.234551 / 1868.600137, 1883.658784 / 1874.607783, 1857.864368 | 316.250, 321.125 / 319.688, 321.594 / 315.281, 317.438 |
| LargeSet / Connections=2 | 1842.925627, 1857.280087 / 1827.886230, 1864.213909 / 1859.508683, 1866.667284 | 313.156, 316.125 / 316.125, 319.938 / 319.688, 314.594 |
| Pipeline / Connections=1 | 6.823286, 6.830047 / 6.852058, 6.811715 / 6.830857, 6.814405 | 6.764, 6.762 / 6.767, 6.763 / 6.767, 6.764 |
| Pipeline / Connections=2 | 7.881797, 7.768363 / 7.743850, 7.732746 / 7.715181, 7.753459 | 6.760, 6.758 / 6.763, 6.757 / 6.735, 6.757 |
| PublicPipeline / Connections=1 | 6.871573, 6.878465 / 6.811591, 6.778370 / 6.817262, 6.798464 | 5.767, 5.767 / 5.770, 5.764 / 5.753, 5.770 |
| PublicPipeline / Connections=2 | 7.841138, 7.835839 / 7.857961, 7.707254 / 7.838924, 7.790233 | 5.773, 5.768 / 5.779, 5.764 / 5.758, 5.763 |
| SelectConnection / Connections=1 | 0.002670, 0.002662 / 0.002748, 0.002678 / 0.002667, 0.002635 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 0.006382, 0.006424 / 0.006401, 0.006435 / 0.006411, 0.006414 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 199.134976, 199.589288 / 197.258394, 197.650391 / 197.385274, 198.030785 | 176.074, 175.684 / 176.029, 175.889 / 175.998, 175.467 |
| SmallSet / Connections=2 | 197.667552, 198.583379 / 198.515084, 197.826413 / 198.992740, 198.381164 | 176.059, 176.119 / 175.213, 176.119 / 175.744, 175.994 |
| StandaloneBatch / Connections=1&count=100 | 358.460952, 361.127569 / 355.665176, 358.205437 / 356.871050, 358.747564 | 30043.012, 30047.129 / 30047.641, 30044.156 / 30051.031, 30047.613 |
| StandaloneBatch / Connections=1&count=32 | 256.947778, 256.857984 / 258.589145, 257.472622 / 257.383196, 257.994908 | 10525.027, 10525.480 / 10523.848, 10523.996 / 10524.664, 10523.906 |
| StandaloneBatch / Connections=2&count=100 | 365.149615, 361.503232 / 356.767975, 357.271106 / 356.353063, 357.500326 | 30046.547, 30042.762 / 30051.480, 30052.898 / 30044.180, 30047.848 |
| StandaloneBatch / Connections=2&count=32 | 263.197378, 261.268948 / 258.336935, 257.002685 / 257.942351, 258.173433 | 10522.086, 10523.660 / 10521.691, 10521.473 / 10525.324, 10525.082 |
| StreamedSetWithDelayedSource / Connections=1 | 10265.535672, 10103.596282 / 10136.786808, 10070.258283 / 10061.261291, 9738.677420 | 12787.375, 12738.125 / 12302.375, 12426.875 / 12704.250, 12679.125 |
| StreamedSetWithDelayedSource / Connections=2 | 9218.926502, 10095.522820 / 9957.462471, 9222.971443 / 9227.220862, 10160.940325 | 12777.875, 12776.250 / 12459.125, 12396.875 / 12739.250, 12790.250 |
| StreamedSetWithInstantSource / Connections=1 | 379.466724, 368.177759 / 376.134873, 371.349985 / 369.399657, 362.492359 | 6508.211, 6519.523 / 6404.621, 6471.941 / 6505.309, 6511.012 |
| StreamedSetWithInstantSource / Connections=2 | 365.642316, 380.306552 / 368.494876, 365.763206 / 369.174642, 384.366143 | 6482.887, 6476.074 / 6322.484, 6491.371 / 6512.391, 6493.004 |

Full-GC process ranges below retain 5,767,168 bytes of caller arrays in every sample. They do not isolate transport or opt-in attempt retention.

| Phase | Connections | Stage | Samples | Managed bytes min–max | POH bytes min–max | Working set min–max | Private memory min–max |
| --- | ---: | --- | ---: | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 22 | 5921336–5921480 | 8184–8184 | 49356800–49754112 | 134430720–134479872 |
| baseline-a | 1 | connected | 22 | 8132128–8152800 | 24528–24528 | 67248128–69328896 | 260366336–260567040 |
| baseline-a | 1 | after-churn | 22 | 8828200–8908024 | 24528–24528 | 69738496–71909376 | 262344704–279457792 |
| baseline-a | 1 | after-dispose | 22 | 7747920–8518864 | 24528–24528 | 74010624–100937728 | 245739520–354590720 |
| baseline-a | 2 | before-connect | 22 | 5921344–5927528 | 8184–8184 | 49446912–49750016 | 134430720–134479872 |
| baseline-a | 2 | connected | 22 | 9208280–9227744 | 24528–24528 | 68513792–68771840 | 262070272–262254592 |
| baseline-a | 2 | after-churn | 22 | 9913280–9994800 | 24528–24528 | 71577600–71966720 | 264626176–281706496 |
| baseline-a | 2 | after-dispose | 22 | 8363304–9201760 | 24528–24528 | 73785344–101961728 | 262086656–338206720 |
| candidate | 1 | before-connect | 22 | 5921448–5927736 | 8184–8184 | 49303552–49623040 | 134438912–134479872 |
| candidate | 1 | connected | 22 | 8136384–8156888 | 24528–24528 | 67096576–67522560 | 260358144–260550656 |
| candidate | 1 | after-churn | 22 | 8900304–8910304 | 24528–24528 | 69664768–70176768 | 262348800–279474176 |
| candidate | 1 | after-dispose | 22 | 7816352–8569224 | 24528–24528 | 73932800–100765696 | 262664192–321363968 |
| candidate | 2 | before-connect | 22 | 5913232–5927720 | 8184–8184 | 49336320–49721344 | 134438912–134479872 |
| candidate | 2 | connected | 22 | 9214720–9235336 | 24528–24528 | 68341760–68820992 | 262045696–262201344 |
| candidate | 2 | after-churn | 22 | 9983896–9993992 | 24528–24528 | 71442432–71962624 | 264622080–281706496 |
| candidate | 2 | after-dispose | 22 | 8379888–9178176 | 24528–24528 | 73527296–101572608 | 262086656–338194432 |
| baseline-b | 1 | before-connect | 22 | 5913112–5921480 | 8184–8184 | 49291264–49651712 | 134434816–134479872 |
| baseline-b | 1 | connected | 22 | 8132128–8152768 | 24528–24528 | 67039232–67661824 | 260313088–260546560 |
| baseline-b | 1 | after-churn | 22 | 8833288–8907528 | 24528–24528 | 69681152–70205440 | 262352896–279441408 |
| baseline-b | 1 | after-dispose | 22 | 7748016–8317752 | 24528–24528 | 73949184–101011456 | 262590464–337641472 |
| baseline-b | 2 | before-connect | 22 | 5913264–5927528 | 8184–8184 | 49446912–49725440 | 134438912–134479872 |
| baseline-b | 2 | connected | 22 | 9210360–9235968 | 24528–24528 | 68509696–68878336 | 262049792–262262784 |
| baseline-b | 2 | after-churn | 22 | 9912896–9991592 | 24528–24528 | 71565312–72048640 | 264613888–281735168 |
| baseline-b | 2 | after-dispose | 22 | 8329032–9359328 | 24528–24528 | 73875456–101625856 | 262029312–338173952 |

POH fragmentation is zero in every sample. Phase CPU user/system seconds and utilization: baseline-a: 776.87 687.11 152%; candidate: 771.73 664.05 153%; baseline-b: 758.12 651.71 152%.

#### Error metrics enabled

validation-baseline: 22 cases, 22 successful exits, 88 retention samples; validation-candidate: 22 cases, 22 successful exits, 88 retention samples; baseline-a: 22 cases, 44 successful exits, 176 retention samples; candidate: 22 cases, 44 successful exits, 176 retention samples; baseline-b: 22 cases, 44 successful exits, 176 retention samples.

All latency intervals below are exported 99.9% intervals in microseconds. Positive deltas mean slower. Allocation is exported B/op. A and B are bracket controls.

| Case / parameters | C/A; C/B | B/A drift | Interval us: A / C / B | B/op: A / C / B |
| --- | ---: | ---: | --- | ---: |
| ClientPing / Connections=1 | -0.49%; +0.04% | -0.53% | 189.741110–190.845470 / 188.736549–189.978804 / 188.570472–189.990101 | 184 / 184 / 184 |
| ClientPing / Connections=2 | -0.15%; +0.21% | -0.36% | 189.770472–191.118968 / 189.462466–190.847431 / 189.077179–190.447090 | 184 / 184 / 184 |
| ClientServerError / Connections=1 | -0.46%; +0.27% | -0.73% | 219.822546–221.565355 / 218.821432–220.534038 / 217.923259–220.250338 | 952 / 952 / 952 |
| ClientServerError / Connections=2 | +0.01%; +0.29% | -0.28% | 219.668446–221.636855 / 219.963227–221.392990 / 219.042716–221.038973 | 952 / 952 / 952 |
| LargeSet / Connections=1 | -0.57%; -0.60% | +0.03% | 1778.645439–1869.620267 / 1761.836494–1865.666749 / 1782.813131–1866.595003 | 323 / 319 / 321 |
| LargeSet / Connections=2 | +1.71%; +0.31% | +1.39% | 1734.083281–1860.842439 / 1775.273030–1881.055404 / 1775.705312–1869.357506 | 321 / 321 / 316 |
| Pipeline / Connections=1 | +1.16%; +0.44% | +0.71% | 6.601205–6.655855 / 6.679107–6.731860 / 6.652778–6.699057 | 7 / 7 / 7 |
| Pipeline / Connections=2 | +0.10%; +0.13% | -0.03% | 7.618753–7.698837 / 7.631445–7.701563 / 7.615971–7.696883 | 7 / 7 / 7 |
| PublicPipeline / Connections=1 | +0.46%; +0.18% | +0.28% | 6.646023–6.709359 / 6.691291–6.726124 / 6.671536–6.721425 | 6 / 6 / 6 |
| PublicPipeline / Connections=2 | -0.20%; +0.09% | -0.29% | 7.669813–7.788120 / 7.661955–7.764734 / 7.666136–7.747188 | 6 / 6 / 6 |
| SelectConnection / Connections=1 | +157.05%; +158.27% | -0.47% | 0.002651–0.002669 / 0.003737–0.009937 / 0.002641–0.002654 | 0 / 0 / 0 |
| SelectConnection / Connections=2 | +0.08%; -0.28% | +0.35% | 0.006383–0.006401 / 0.006381–0.006413 / 0.006394–0.006436 | 0 / 0 / 0 |
| SmallSet / Connections=1 | +0.53%; +0.06% | +0.47% | 191.527661–192.912295 / 192.829885–193.647724 / 192.660536–193.585266 | 176 / 176 / 176 |
| SmallSet / Connections=2 | +0.15%; +0.28% | -0.13% | 193.629963–194.569910 / 193.705516–195.068694 / 193.248883–194.454819 | 176 / 176 / 175 |
| StandaloneBatch / Connections=1&count=100 | -0.15%; +0.13% | -0.29% | 354.052362–359.757835 / 355.337275–357.389096 / 354.601540–357.164353 | 30057 / 30048 / 30046 |
| StandaloneBatch / Connections=1&count=32 | +0.05%; -0.09% | +0.14% | 253.027093–254.362721 / 253.147125–254.516022 / 253.233855–254.885760 | 10525 / 10525 / 10519 |
| StandaloneBatch / Connections=2&count=100 | +0.50%; +0.73% | -0.24% | 352.893709–355.749842 / 354.435747–357.720722 / 351.863788–355.108201 | 30049 / 30049 / 30054 |
| StandaloneBatch / Connections=2&count=32 | +0.06%; +0.27% | -0.22% | 252.543445–254.375615 / 252.609347–254.603137 / 252.143424–253.682133 | 10525 / 10521 / 10525 |
| StreamedSetWithDelayedSource / Connections=1 | -5.56%; -4.53% | -1.08% | 10048.832685–10140.827699 / 9297.069753–9770.091732 / 9921.697215–10049.416793 | 12665 / 12482 / 12784 |
| StreamedSetWithDelayedSource / Connections=2 | -0.25%; +1.07% | -1.31% | 9363.236908–10048.678745 / 9344.946925–10019.132710 / 9357.123259–9801.076143 | 12743 / 12492 / 12755 |
| StreamedSetWithInstantSource / Connections=1 | -0.59%; +0.03% | -0.63% | 362.881535–373.540573 / 361.531325–370.525026 / 362.318611–369.495764 | 6518 / 6455 / 6496 |
| StreamedSetWithInstantSource / Connections=2 | -0.13%; +0.63% | -0.75% | 362.830346–372.914014 / 363.380060–371.433543 / 360.755005–369.473849 | 6519 / 6483 / 6504 |

Every raw actual workload sample and retained result sample was checked for its case, phase, launch and operation count. Each row has two launches and 30 actual workload samples. The table reports each launch mean from overhead-adjusted workload results and each raw `// GC:` bytes/operations ratio; these process launches are not independent repetitions of the whole bracket experiment.

| Case / parameters | Launch 1, 2 mean us: A / C / B | Raw launch 1, 2 B/op: A / C / B |
| --- | --- | --- |
| ClientPing / Connections=1 | 190.308982, 190.277598 / 189.636632, 189.078720 / 189.993696, 188.617834 | 184.090, 183.793 / 184.045, 183.545 / 184.043, 183.639 |
| ClientPing / Connections=2 | 190.667751, 190.205758 / 190.187678, 190.122219 / 189.901916, 189.622353 | 184.119, 184.119 / 184.135, 184.135 / 183.963, 184.090 |
| ClientServerError / Connections=1 | 220.812790, 220.575111 / 219.032310, 220.280132 / 218.688965, 219.484632 | 952.148, 952.148 / 952.148, 952.148 / 952.207, 952.297 |
| ClientServerError / Connections=2 | 220.415977, 220.873545 / 220.113608, 221.167342 / 219.684702, 220.373245 | 952.387, 952.193 / 952.387, 952.193 / 952.193, 952.387 |
| LargeSet / Connections=1 | 1817.710394, 1831.014059 / 1837.776022, 1789.727220 / 1829.565707, 1820.490646 | 319.094, 322.656 / 318.625, 319.094 / 317.312, 321.469 |
| LargeSet / Connections=2 | 1764.446315, 1830.479405 / 1842.953594, 1813.374840 / 1806.352189, 1838.710629 | 317.438, 320.875 / 310.188, 321.469 / 319.094, 315.531 |
| Pipeline / Connections=1 | 6.601909, 6.653249 / 6.697427, 6.713539 / 6.687520, 6.664315 | 6.761, 6.761 / 6.761, 6.755 / 6.750, 6.761 |
| Pipeline / Connections=2 | 7.665419, 7.651697 / 7.677476, 7.655532 / 7.631105, 7.680061 | 6.745, 6.764 / 6.766, 6.751 / 6.750, 6.766 |
| PublicPipeline / Connections=1 | 6.687592, 6.667083 / 6.708240, 6.709145 / 6.718961, 6.672394 | 5.761, 5.761 / 5.764, 5.765 / 5.757, 5.763 |
| PublicPipeline / Connections=2 | 7.753801, 7.705906 / 7.729114, 7.696449 / 7.701387, 7.711937 | 5.758, 5.766 / 5.760, 5.758 / 5.765, 5.768 |
| SelectConnection / Connections=1 | 0.002663, 0.002657 / 0.010998, 0.002677 / 0.002655, 0.002640 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SelectConnection / Connections=2 | 0.006395, 0.006390 / 0.006382, 0.006411 / 0.006402, 0.006427 | 0.000, 0.000 / 0.000, 0.000 / 0.000, 0.000 |
| SmallSet / Connections=1 | 192.271417, 192.168540 / 193.353560, 193.124050 / 192.931589, 193.300549 | 176.090, 176.074 / 176.090, 175.949 / 176.074, 176.074 |
| SmallSet / Connections=2 | 194.294428, 193.931376 / 194.269656, 194.504554 / 193.935996, 193.754761 | 176.135, 175.691 / 175.994, 175.525 / 176.104, 175.494 |
| StandaloneBatch / Connections=1&count=100 | 354.015529, 360.001065 / 357.252465, 355.405499 / 355.315502, 356.450391 | 30049.641, 30056.664 / 30048.430, 30048.098 / 30051.156, 30046.008 |
| StandaloneBatch / Connections=1&count=32 | 253.570307, 253.819507 / 253.768951, 253.890021 / 254.425362, 253.668142 | 10525.238, 10524.754 / 10500.855, 10524.602 / 10524.328, 10518.926 |
| StandaloneBatch / Connections=2&count=100 | 354.166404, 354.466049 / 354.955275, 357.281406 / 353.255991, 353.732427 | 30053.203, 30048.875 / 30040.852, 30049.453 / 30042.035, 30054.387 |
| StandaloneBatch / Connections=2&count=32 | 254.137129, 252.733530 / 252.763764, 254.508898 / 253.014345, 252.817984 | 10522.445, 10525.051 / 10524.566, 10521.418 / 10521.023, 10524.656 |
| StreamedSetWithDelayedSource / Connections=1 | 10105.295392, 10083.617479 / 9873.600018, 9193.561468 / 10038.574498, 9932.539510 | 12761.375, 12664.750 / 12577.875, 12481.625 / 12678.750, 12784.500 |
| StreamedSetWithDelayedSource / Connections=2 | 9198.747146, 10179.354461 / 9175.783582, 10120.795222 / 9732.898059, 9425.301343 | 12803.500, 12742.750 / 12426.875, 12492.000 / 12728.875, 12754.625 |
| StreamedSetWithInstantSource / Connections=1 | 366.004681, 370.270335 / 366.871992, 365.184359 / 368.071869, 363.742506 | 6516.906, 6518.141 / 6486.629, 6455.258 / 6500.750, 6496.148 |
| StreamedSetWithInstantSource / Connections=2 | 367.529122, 368.215238 / 365.028167, 369.785437 / 366.059203, 364.102167 | 6500.883, 6518.863 / 6515.953, 6483.121 / 6502.082, 6503.703 |

Full-GC process ranges below retain 5,767,168 bytes of caller arrays in every sample. They do not isolate transport or opt-in attempt retention.

| Phase | Connections | Stage | Samples | Managed bytes min–max | POH bytes min–max | Working set min–max | Private memory min–max |
| --- | ---: | --- | ---: | --- | --- | --- | --- |
| baseline-a | 1 | before-connect | 22 | 5913112–5921480 | 8184–8184 | 49348608–49594368 | 134434816–134479872 |
| baseline-a | 1 | connected | 22 | 8132512–8153360 | 24528–24528 | 67108864–67526656 | 260378624–260542464 |
| baseline-a | 1 | after-churn | 22 | 8832784–8908560 | 24528–24528 | 69726208–70111232 | 262393856–279461888 |
| baseline-a | 1 | after-dispose | 22 | 7813880–8562400 | 24528–24528 | 74059776–100999168 | 262565888–355008512 |
| baseline-a | 2 | before-connect | 22 | 5913120–5927632 | 8184–8184 | 49299456–49709056 | 134438912–134479872 |
| baseline-a | 2 | connected | 22 | 9215056–9228304 | 24528–24528 | 68464640–68878336 | 262086656–279117824 |
| baseline-a | 2 | after-churn | 22 | 9913832–9992088 | 24528–24528 | 71512064–72081408 | 264617984–281718784 |
| baseline-a | 2 | after-dispose | 22 | 8303872–9153600 | 24528–24528 | 74190848–101441536 | 262684672–334082048 |
| candidate | 1 | before-connect | 22 | 5913224–5921592 | 8184–8184 | 49295360–49668096 | 134434816–134479872 |
| candidate | 1 | connected | 22 | 8136760–8153408 | 24528–24528 | 67006464–67555328 | 260399104–260562944 |
| candidate | 1 | after-churn | 22 | 8900944–8913472 | 24528–24528 | 69611520–70144000 | 262393856–279482368 |
| candidate | 1 | after-dispose | 22 | 7811600–8526872 | 24528–24528 | 73969664–100229120 | 245919744–320610304 |
| candidate | 2 | before-connect | 22 | 5913232–5921600 | 8184–8184 | 49295360–49627136 | 134438912–134479872 |
| candidate | 2 | connected | 22 | 9204576–9227560 | 24528–24528 | 68472832–68833280 | 262115328–262275072 |
| candidate | 2 | after-churn | 22 | 9982768–9993968 | 24528–24528 | 71548928–71929856 | 264675328–281722880 |
| candidate | 2 | after-dispose | 22 | 8371664–9154800 | 24528–24528 | 73928704–101756928 | 262672384–355229696 |
| baseline-b | 1 | before-connect | 22 | 5913112–5927496 | 8184–8184 | 49393664–49684480 | 134434816–134479872 |
| baseline-b | 1 | connected | 22 | 8132560–8153296 | 24528–24528 | 67145728–67518464 | 260349952–260542464 |
| baseline-b | 1 | after-churn | 22 | 8833280–8907368 | 24528–24528 | 69799936–70111232 | 262475776–279461888 |
| baseline-b | 1 | after-dispose | 22 | 7814472–8344448 | 24528–24528 | 73928704–100597760 | 245866496–371646464 |
| baseline-b | 2 | before-connect | 22 | 5913264–5927488 | 8184–8184 | 49410048–49758208 | 134434816–134479872 |
| baseline-b | 2 | connected | 22 | 9204568–9228064 | 24528–24528 | 68489216–68874240 | 262062080–262193152 |
| baseline-b | 2 | after-churn | 22 | 9913464–9993376 | 24528–24528 | 71536640–72011776 | 264609792–281673728 |
| baseline-b | 2 | after-dispose | 22 | 8357736–9391720 | 24528–24528 | 74043392–101466112 | 262402048–339402752 |

POH fragmentation is zero in every sample. Phase CPU user/system seconds and utilization: baseline-a: 749.45 642.06 153%; candidate: 791.86 688.88 154%; baseline-b: 767.04 650.13 153%.

## Standalone batch allocation attribution (#1479)

`StandaloneBatch` does not call `WaitForFinalFrameWriteAsync`. The historical enabled
one-connection/100 measurements of 30015.758 and 30016.531 B/op, versus controls
30013.080–30014.564 B/op, remain unattributed. The disabled two-connection/100
second-launch drop also remains unattributed. Those artifacts have no allocation
stacks or suspension counts, so they cannot establish which object, pool miss,
reply schedule, or background activity caused either difference. The final-frame
builder change is not a demonstrated batch cause. These differences are not
classified as noise, and totals from different fixtures are not subtracted.

The diagnostic command adds evidence for future launches independently of public
timing. It runs the existing public fixture, including its raw multiplexer, ordinary
client, circuit client, setup traffic and selected error metric mode. A second
client supplies only the explicitly named receive-gate controls. Every invocation
records the full library revision, fixture/library assembly SHA-256, metric mode,
case, PID, runtime and launch. The trace report embeds that identity and preserves
allocation timestamps, thread IDs, sampled types, object sizes and resolved stacks.

| Control | Causal boundary | Attribution supported |
| --- | --- | --- |
| `construction` | Queue the same INCR commands; retain batches and dispose after the boundary | Batch/facet creation, pending/operation construction and growing operation arrays, without sending or reply completion |
| `public-task` | Call the unchanged public `StandaloneBatch` method | Natural reply scheduling, including the fixture's `Task<long>` and its observed incomplete-at-call count |
| `fixture-pooled-valuetask` | Same batch construction and execution with a warmed pooling builder on the diagnostic wrapper | Remove the unpooled fixture Task while retaining transport and pending work; identify wrapper allocation stacks rather than subtracting process totals |
| `gated-first` | First batch on a new diagnostic client; hold real received bytes before parsing | Prove execution is incomplete before releasing replies; new-client startup is visible, not a promise of an empty process-wide pool |
| `gated-pool-demand` | Construct outside the boundary, then hold 128 batch executions concurrently | Require simultaneously live completion/builder state; observe pool-growth allocations independently of construction. At most 12,800 commands fit the unchanged default 16,384-command limit |
| `gated-direct` / `gated-task` | Hold replies for each batch, then release and consume execution and pending results | Distinguish direct execution from an added Task wrapper under a forced reply suspension |
| `idle-before` / `idle-after` | Keep the original fixture connected for 250 ms without batch traffic | Expose sampled background/collector activity; absence of a sample does not prove zero background allocation |
| `background-positive` | Queue 128 escaping 64 KiB arrays on a worker without batch traffic | Calibrate process-wide capture with a known allocation type and distinct worker stack, without assigning that work to transport |

The socket gate forwards real writes unchanged and holds bytes after the socket
read, before the RESP parser. Its own TaskCompletionSource, linked cancellation,
wait timer and stream state are diagnostic overhead with identifiable stacks.
Gate tests exercise 32/100 commands on one/two connections, repeated use, FIFO
pending results, and disposal while a read is held. The direct public controls
continue to use the normal socket transport. No production transport code changes.

Allocation ticks sample objects, not every allocation. `allocationIntervalBytes`
is allocation since a tick, including other types and potentially an earlier
control; it must never be charged to the sampled type. `sampledObjectBytes` is the
sampled object's size. Process-counter observations include tracing, collector,
controller, and background work. Construction counters report collections during
the synchronous no-inline boundary and make no zero-allocation assertion. Internal
reply-suspension and pool-miss counts are not instrumented: each gated execution
proves at least one incomplete reply path, while stacks identify sampled builders.
Report summaries expose missing type/stack samples and lost events. Conversion
fails on dropped events, missing boundaries, mismatched process identity, or no
usable type/stack evidence. Unsampled objects remain unavailable for attribution.

For local diagnostics, commit measured source, point `REDIS_HOST` and `REDIS_PORT`
at an owned test server, then run:

```powershell
pwsh scripts/Invoke-TransportBatchDiagnostics.ps1 -OutputDirectory ../batch-evidence
```

The script rebuilds the selected framework through the unchanged agent guard
before capturing evidence, so an old DLL cannot receive the current revision
stamp. It rejects revision or measured-source changes during the rebuild. It
captures two separate processes for each 32/100-command, one/two-connection case
in both metric modes. Use `-Framework net8.0` for correctness/diagnostic
validation. This is not a local benchmark suite. The label-triggered transport
workflow remains net10.0 only and copies the
same diagnostic sources to both pinned libraries. It runs all public timings
first, then captures baseline/candidate/baseline diagnostic processes with the
same case/launch coverage. Artifacts retain `metadata.json`, `allocations.nettrace`,
`allocations.jsonl`, and per-launch logs under `results/batch-allocation`.
Diagnostic launch numbers identify their own processes; they are not the earlier
BenchmarkDotNet timing launch numbers. These traces cannot retrospectively explain
the historical deltas or automatically establish aggregate performance acceptance.
The final #1474 audit owns that decision; #1214 and #862 remain open.

### Local causal evidence: 2026-10-12

Revision `49ecd977ef46072936f0743475567581a93706ee` ran on Windows
10.0.26200 with an owned Redis 8.10 container. Each launch used 128 iterations per
ordinary control, one first gated batch, 128 overlapping batches, and 128 background
arrays. Both runtimes captured all controls and proved every gated operation
incomplete before release. These are diagnostic executions, not public latency
measurements. The .NET 10 fixture/library SHA-256 values were
`16AB29EEAF7C3B81FF8ED7311D7AF76308329FAC777494ADE11D612F27753A7A` /
`F0E695A5FA66666D9CC189EC9442CE695F6ED25D1BB468D9A3810F3B07D18731`;
the .NET 8 values were
`AED8B102E3D9316D5E7D90CE74A9E818F89FDB6D527E0E77DF5B956CC3E4325A` /
`C8709B2BF72EC159C26C6F518ABD0AEA8B852AD3B7719810C65F3E92422F2AE8`.

The table identifies every case and process. Sample counts are allocation ticks;
all 5,947 .NET 10 and 6,741 .NET 8 samples had types and stacks, with zero dropped
events. Every trace contained 64 sampled 65,560-byte arrays with the
`AllocateBackgroundControl` worker stack, independently of transport.

| Error metrics | Connections/commands | Launch | .NET 10.0.12 PID / samples | .NET 8.0.31 PID / samples |
| --- | --- | --- | --- | --- |
| disabled | 1/32 | 1 | 32708 / 193 | 5428 / 242 |
| disabled | 1/32 | 2 | 7972 / 242 | 34824 / 242 |
| disabled | 1/100 | 1 | 2972 / 350 | 19600 / 597 |
| disabled | 1/100 | 2 | 38668 / 350 | 11756 / 596 |
| disabled | 2/32 | 1 | 37508 / 245 | 17236 / 246 |
| disabled | 2/32 | 2 | 15364 / 245 | 3400 / 246 |
| disabled | 2/100 | 1 | 10724 / 600 | 37640 / 601 |
| disabled | 2/100 | 2 | 39824 / 598 | 38676 / 600 |
| enabled | 1/32 | 1 | 37436 / 242 | 37840 / 242 |
| enabled | 1/32 | 2 | 12928 / 241 | 3452 / 242 |
| enabled | 1/100 | 1 | 30960 / 596 | 516 / 596 |
| enabled | 1/100 | 2 | 3140 / 596 | 21216 / 597 |
| enabled | 2/32 | 1 | 29292 / 247 | 3840 / 245 |
| enabled | 2/32 | 2 | 33124 / 246 | 33012 / 247 |
| enabled | 2/100 | 1 | 37620 / 357 | 35428 / 603 |
| enabled | 2/100 | 2 | 22556 / 599 | 37696 / 599 |

Concrete .NET 10 sampled objects distinguish the requested sites. The sizes below
are individual objects, never totals assigned to a type or per-batch differences:

| Case / PID | Control | Sampled type / size | Allocation stack evidence |
| --- | --- | --- | --- |
| disabled, 1/32, launch 1 / 32708 | construction | `RespirePending<long>` / 64 B | `RespireBatch.Add`, `BatchStringCommands.Increment`, `Construct` |
| disabled, 1/32, launch 1 / 32708 | construction | `Op<IncrementCommand,long>` / 224 B | `RespireBatch.Add`, `BatchStringCommands.Increment`, `Construct` |
| disabled, 1/100, launch 1 / 2972 | construction | `Op[]` / 1,048 B | `List<Op>.AddWithResize`, `Construct` |
| disabled, 1/32, launch 1 / 32708 | public-task | `AsyncStateMachineBox<long,StandaloneBatch>` / 152 B | `AsyncTaskMethodBuilder<long>.GetStateMachineBox`, `StandaloneBatch.MoveNext` |
| disabled, 1/32, launch 2 / 7972 | gated-direct | `AsyncStateMachineBox<RespireBatchResult,TryExecuteAsync>` / 328 B | `AsyncTaskMethodBuilder.GetStateMachineBox`, `TryExecuteAsync.MoveNext`, `ExecuteAsync.MoveNext` |
| disabled, 1/32, launch 2 / 7972 | gated-pool-demand | pooling `StateMachineBox<Exception,CompleteSendAsync>` / 240 B | `PoolingAsyncValueTaskMethodBuilder.StateMachineBox.RentFromCache`, `GetStateMachineBox`, `CompleteSendAsync.MoveNext` |
| disabled, 1/32, launch 1 / 32708 | background-positive | `byte[]` / 65,560 B | `AllocateBackgroundControl`, `Task<int>.InnerInvoke`, `ThreadPoolWorkQueue.Dispatch` |

The construction-only boundary demonstrates existing pending/operation/list
allocations independently of response work. Holding replies demonstrates a real
completion suspension; keeping 128 executions live exposes a sampled pooling
builder miss. The public fixture Task has its own sampled allocation stack, and
the diagnostic pooling-wrapper control removes that unpooled wrapper. The worker
positive control demonstrates why process totals require stack attribution.
These observations establish concrete allocation sites and controlled causes;
they do not identify the objects behind the historical fractional B/op deltas.
No causal transport defect or zero-allocation contract violation was demonstrated,
so this change does not alter production builders or transport behavior.

Benchmark projects built on net8.0/net10.0 through the unchanged guard. The five
gate tests passed on each runtime in a focused harness compiling the committed
gate, fake-server and test sources. The full `Respire.Tests` build reached the
unchanged 2,048 MB process-tree guard (observed 2,142 MB), so full-project validation
remains for CI. A negative control that changed only metadata PID failed conversion
with `Trace contains mismatched process identity or duplicate control boundaries.`
The website build and workflow YAML/Python/PowerShell syntax checks passed.
Raw local evidence is retained outside the released worktree; the requested pinned
workflow publishes fresh, independently identified evidence for the later #1474
audit. Its pending comparison is not a passing aggregate disposition.
