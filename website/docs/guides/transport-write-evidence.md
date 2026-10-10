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
