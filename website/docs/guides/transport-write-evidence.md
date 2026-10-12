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

For local diagnostics, commit measured source, build the benchmark project through
the unchanged agent guard, point `REDIS_HOST` and `REDIS_PORT` at an owned test
server, then run:

```powershell
& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'build', 'benchmarks/Respire.Benchmarks/Respire.Benchmarks.csproj', '-c', 'Release', '-f', 'net10.0')
pwsh scripts/Invoke-TransportBatchDiagnostics.ps1 -OutputDirectory ../batch-evidence
```

The script captures two separate processes for each 32/100-command,
one/two-connection case in both metric modes. Use `-Framework net8.0` after a
net8.0 build for correctness/diagnostic validation. This is not a local benchmark
suite. The label-triggered transport workflow remains net10.0 only and copies the
same diagnostic sources to both pinned libraries. It runs all public timings
first, then captures baseline/candidate/baseline diagnostic processes with the
same case/launch coverage. Artifacts retain `metadata.json`, `allocations.nettrace`,
`allocations.jsonl`, and per-launch logs under `results/batch-allocation`.
Diagnostic launch numbers identify their own processes; they are not the earlier
BenchmarkDotNet timing launch numbers. These traces cannot retrospectively explain
the historical deltas or automatically establish aggregate performance acceptance.
The final #1474 audit owns that decision; #1214 and #862 remain open.
