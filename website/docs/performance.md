---
title: Performance
description: Measured Linux comparisons with StackExchange.Redis 3.4.5 and 4.0.127-alpha, including limitations and reproducible evidence.
---

import useBaseUrl from '@docusaurus/useBaseUrl';

# Performance

In the original Linux Redis-container measurements, Respire is faster for concurrent and
pipelined GETs against both StackExchange.Redis versions. A second 3.4.5 run retains the
concurrent advantage but classifies pipelined GET as `Same`. Most single-operation timings
against 3.4.5 show no established latency advantage. Against 4.0.127-alpha, the ordinary-operation and
local GET cache-hit comparisons favor Respire. Publish-to-consume pub/sub shows no established
latency advantage: the latest runs tie both versions, and an earlier 4.0 run was slower.
These results describe the measured commits and rigs, not a guarantee for every workload.

## Measured comparisons: October 2026

Each table compares clients **within the same run**. The 3.4.5 and 4.0.127-alpha runs used
different hosted machines; do not compare their absolute timings to infer a version regression.
Likewise, the replacement pub/sub runs cannot isolate the cost of the delivery-gap guard.
4.0.127-alpha is a prerelease dependency, not a claim about every 4.0 release.

Timings below are **mean ± BenchmarkDotNet Error** (half the 99.9% confidence interval).
`Faster`, `Same`, and `Slower` reproduce the report's configured `MannWhitney(5%)` comparison
against StackExchange.Redis. `Same` means this comparison did not establish a difference;
it is not proof of equivalence. A lower point estimate alone is not called a win.
The evidence archives retain standard deviations, medians where emitted, raw measurements,
outliers, allocation data, and full logs. Allocation figures are measured bytes per operation;
zero means no allocation reported by this measurement, not a universal API guarantee.

### Single operations, sequential loops, and large values

`CommonOperationsBenchmarks` uses uncached server operations. Times are in **microseconds**.
The x100 sequential rows are normalized per command; LPUSH+LPOP and SET+DEL measure the
whole two-command sequence. The SET payloads named 1KB and 10KB contain 1,024 and 10,240
ASCII bytes. This suite does not measure large-value GET or arbitrary payload sizes.

#### StackExchange.Redis 3.4.5

| Operation | Respire μs | StackExchange μs | Allocated Respire / StackExchange | Respire classification |
| --- | --- | --- | --- | --- |
| EXISTS | 108.120 ± 0.8404 | 109.080 ± 0.5449 | 0 B / 296 B | Same |
| GET | 108.447 ± 0.6875 | 108.916 ± 0.7565 | 48 B / 504 B | Same |
| GET long | 108.388 ± 0.4209 | 108.991 ± 0.4405 | 0 B / 416 B | Same |
| GET x100 sequential | 89.151 ± 1.0186 | 93.943 ± 0.9317 | 50 B / 338 B | Same |
| HGET | 108.143 ± 0.6267 | 108.667 ± 0.8045 | 48 B / 520 B | Same |
| HSET | 109.850 ± 0.4958 | 109.674 ± 0.5902 | 0 B / 328 B | Same |
| INCR | 108.765 ± 0.5386 | 109.459 ± 0.5544 | 0 B / 296 B | Same |
| LPUSH+LPOP | 205.466 ± 1.7639 | 207.979 ± 1.2407 | 256 B / 760 B | Same |
| PING | 108.190 ± 0.5336 | 108.415 ± 0.5876 | 0 B / 303 B | Same |
| PING x100 sequential | 88.859 ± 1.1063 | 92.409 ± 0.9367 | 2 B / 242 B | Same |
| SADD | 108.965 ± 0.6074 | 109.482 ± 0.6475 | 0 B / 312 B | Same |
| SET 10KB | 112.989 ± 0.8238 | 115.088 ± 1.3919 | 0 B / 312 B | Same |
| SET 13B | 109.257 ± 0.6119 | 110.387 ± 0.6958 | 0 B / 312 B | Same |
| SET 1KB | 109.930 ± 0.7464 | 110.148 ± 0.8972 | 0 B / 312 B | Same |
| SET x100 sequential | 91.513 ± 1.0724 | 96.416 ± 1.4405 | 2 B / 250 B | Same |
| SET+DEL | 201.940 ± 1.0721 | 205.198 ± 1.0812 | 200 B / 648 B | Same |
| SISMEMBER | 108.239 ± 0.7833 | 109.688 ± 0.4727 | 0 B / 312 B | Same |

#### StackExchange.Redis 4.0.127-alpha

| Operation | Respire μs | StackExchange μs | Allocated Respire / StackExchange | Respire classification |
| --- | --- | --- | --- | --- |
| EXISTS | 97.716 ± 2.2846 | 113.468 ± 0.5487 | 0 B / 518 B | Faster |
| GET | 90.781 ± 2.2935 | 113.737 ± 1.5128 | 48 B / 718 B | Faster |
| GET long | 95.943 ± 1.7497 | 114.927 ± 0.6654 | 0 B / 632 B | Faster |
| GET x100 sequential | 81.109 ± 1.2698 | 92.215 ± 2.3050 | 50 B / 205 B | Faster |
| HGET | 101.495 ± 2.1170 | 116.696 ± 0.8607 | 48 B / 718 B | Faster |
| HSET | 103.962 ± 2.9925 | 115.521 ± 1.0345 | 0 B / 520 B | Faster |
| INCR | 98.503 ± 2.6063 | 115.415 ± 0.9373 | 0 B / 519 B | Faster |
| LPUSH+LPOP | 198.477 ± 1.5328 | 225.154 ± 1.8793 | 256 B / 858 B | Faster |
| PING | 92.371 ± 2.7828 | 112.531 ± 0.8281 | 0 B / 541 B | Faster |
| PING x100 sequential | 79.714 ± 0.9307 | 90.173 ± 0.9416 | 2 B / 133 B | Faster |
| SADD | 98.382 ± 1.5969 | 113.370 ± 0.6944 | 0 B / 519 B | Faster |
| SET 10KB | 118.639 ± 0.4647 | 131.878 ± 0.7220 | 0 B / 432 B | Faster |
| SET 13B | 99.405 ± 2.5217 | 114.985 ± 1.7170 | 0 B / 520 B | Faster |
| SET 1KB | 102.708 ± 2.3269 | 119.035 ± 0.7005 | 0 B / 432 B | Faster |
| SET x100 sequential | 81.584 ± 1.1433 | 95.635 ± 1.1188 | 2 B / 109 B | Faster |
| SET+DEL | 191.925 ± 1.5237 | 219.893 ± 2.2582 | 200 B / 729 B | Faster |
| SISMEMBER | 100.462 ± 1.9337 | 113.863 ± 0.7926 | 0 B / 520 B | Faster |

The 4.0 run warns that Respire HGET and HSET have multimodal distributions. Their reported
means favor Respire, but that variability limits interpretation; inspect the preserved samples
and standard deviations (3.169 μs for HGET, 4.479 μs for HSET) before extrapolating.
All 17 original ordinary-operation ties against 3.4.5, including SET 10KB, are dispositioned
in the [server-latency investigation below](#server-latency-investigation-outcome).
The older [10KB SET investigation](https://github.com/thomhurst/Respire/issues/1553) concerned
a different, noisy run; these measurements do not establish a continuing 15% regression.

The comparison workflow had an opt-in `server-latency-diagnostics` dispatch input for
that investigation; it was removed once the investigation closed, and the tooling remains
in [09e062d8](https://github.com/thomhurst/Respire/commit/09e062d8). It ran the unchanged CommonOperationsBenchmarks methods and the
four uncached ClientSideCachingBenchmarks pairs, including concurrent/pipelined GET
controls, with the selected BenchmarkDotNet job and the existing 5% statistical test.
Use the `3.4.5` dependency and `medium` job when reproducing these ties.

Separate diagnostic processes call those same methods after warmup, recording process
CPU time, wall time, process-wide allocations, assembly hashes and endpoint identity.
On one runner, a host-network Redis control is bracketed by two Docker port-mapped
passes using the same image ID. A fourth pass captures managed CPU stacks, runtime
threading/contention events and raw EventPipe traces. Artifacts retain all 46 cases
(21 pairs plus two control pairs); missing cases or unusable CPU traces fail the run.

These async-loop diagnostics are not BenchmarkDotNet measurements. Their process totals
include fixture background clients and harness work, and the traced pass includes profiler
overhead. Summed CPU time across threads cannot be subtracted from elapsed time to infer
waiting; managed samples cannot attribute kernel scheduling or Redis server CPU. The
host-network comparison changes the network path and port together and can suggest where
to investigate, but cannot alone prove a scheduler cause. The completed analysis follows.

### Server-latency investigation outcome

[#1744](https://github.com/thomhurst/Respire/issues/1744) concludes with **no production
optimization justified by this profile**. The repeat contains 16 remaining ties and five
`Faster` results among the original 21 ties. Runtime scheduling paths dominate the managed
sample counts, and the separate topology control has a much larger effect than the
between-client differences. This supports a substantial environment/runtime contribution;
it does not prove that client overhead is zero or identify a removable critical-path cost.
The remaining ties are a documented limitation, not deferred validation or claimed wins.

The hosted [repeat run 38695907419](https://github.com/thomhurst/Respire/actions/runs/38695907419)
measured source commit
[`8fd9ea63b49b6e13981c674f2f4d6414dc248fa0`](https://github.com/thomhurst/Respire/tree/8fd9ea63b49b6e13981c674f2f4d6414dc248fa0)
against **StackExchange.Redis 3.4.5**, on an **AMD EPYC 9V74**, four logical/two physical cores,
Ubuntu **24.04.5**, kernel **6.17.0-1022-azure**, runner image **ubuntu24 20261004.327.1**.
It used **net10.0**, SDK **10.0.402**, runtime **10.0.13**, BenchmarkDotNet **0.15.8**,
X64 RyuJIT x86-64-v4, Medium/**two launches**, **10 warmups** and **15 measurement iterations**
per launch. The benchmark filter selected `*CommonOperationsBenchmarks.*` and
`*ClientSideCachingBenchmarks.*ServerRead`; neither workload nor `MannWhitney(5%)` changed.
Redis was **8.10.2**, with the same image ID recorded in the original provenance below.
The original run used an Intel Xeon Platinum 8573C and a different Respire commit. Thus these
are **original/repeat observations, not a controlled before/after optimization experiment**.
No source speedup is inferred from a change in absolute timing or classification.

All 21 original ties and both regression controls appear below. Times are **mean ± Error
in μs**, and allocations are **bytes per operation** (Respire / StackExchange). The original
means and allocations remain in the preceding and following historical tables. Each
classification compares clients only within its own run. Sequential and overlapping GET
normalization is unchanged; overlapping GET times are amortized per completed command.

| Scenario | Respire μs | StackExchange μs | Allocated R / SE | Original → repeat |
| --- | ---: | ---: | ---: | --- |
| EXISTS | 95.861 ± 3.9044 | 102.744 ± 0.6235 | 0 / 295 B | Same → Same |
| GET | 99.934 ± 1.8565 | 110.771 ± 1.2219 | 48 / 502 B | Same → Faster |
| GET long | 100.312 ± 2.3864 | 109.751 ± 0.8271 | 0 / 415 B | Same → Faster |
| GET x100 sequential | 80.970 ± 0.3863 | 85.980 ± 0.6770 | 50 / 338 B | Same → Same |
| GET x200 pipelined | 1.407 ± 0.0072 | 1.514 ± 0.0385 | 49 / 289 B | Faster → Same |
| GET x50 concurrent | 3.055 ± 0.0191 | 3.324 ± 0.0523 | 52 / 291 B | Faster → Faster |
| HGET | 99.962 ± 1.8843 | 109.106 ± 1.4243 | 48 / 520 B | Same → Faster |
| HSET | 104.186 ± 1.6189 | 104.983 ± 1.0589 | 0 / 328 B | Same → Same |
| INCR | 98.713 ± 2.4484 | 100.479 ± 2.0274 | 0 / 296 B | Same → Same |
| LPUSH+LPOP | 200.581 ± 1.3785 | 207.913 ± 3.7299 | 256 / 760 B | Same → Same |
| PING | 93.805 ± 3.4263 | 97.065 ± 2.1578 | 0 / 303 B | Same → Same |
| PING x100 sequential | 78.143 ± 0.6845 | 82.237 ± 0.9688 | 2 / 242 B | Same → Same |
| SADD | 100.800 ± 1.8099 | 96.493 ± 4.1928 | 0 / 312 B | Same → Same |
| SET 10KB | 120.382 ± 1.1587 | 117.864 ± 0.7227 | 0 / 312 B | Same → Same |
| SET 13B | 103.051 ± 1.3332 | 103.948 ± 0.8217 | 0 / 311 B | Same → Same |
| SET 1KB | 101.637 ± 2.1314 | 105.679 ± 1.0368 | 0 / 312 B | Same → Same |
| SET x100 sequential | 83.516 ± 0.5746 | 86.249 ± 0.5078 | 2 / 250 B | Same → Same |
| SET+DEL | 192.682 ± 1.2953 | 207.056 ± 1.5008 | 200 / 648 B | Same → Faster |
| SISMEMBER | 101.225 ± 1.6174 | 98.654 ± 1.6609 | 0 / 312 B | Same → Same |
| Uncached / EXISTS hot | 100.552 ± 1.510 | 99.756 ± 1.884 | 0 / 296 B | Same → Same |
| Uncached / GET hot | 101.595 ± 1.200 | 107.830 ± 2.711 | 64 / 527 B | Same → Same |
| Uncached / GET missing hot | 101.998 ± 1.730 | 109.350 ± 1.306 | 0 / 416 B | Same → Same |
| Uncached / HGET hot | 103.874 ± 0.757 | 111.563 ± 0.974 | 64 / 543 B | Same → Faster |

Every `Same → Same` row retains the limitation: no latency advantage was established.
The five `Same → Faster` rows establish an advantage only in this repeat, without attributing
it to a code change. No row is `Slower`. The pipelined control **loses its established
advantage**, despite a lower point estimate; this must not be hidden by the concurrent
control's `Faster` result. Neither control establishes a historical regression across rigs.
Respire's reported allocations match the original run in all 23 scenarios, and remain below
StackExchange's; that does not override any latency tie. The repeat warns of bimodality for
Respire EXISTS and StackExchange SADD; the archived logs and samples retain that variation.

#### Separating process work from topology effects

The diagnostics use fresh processes, two seconds of warmup and three seconds of measurement
per case, separately from BDN. The same Redis image runs first through Docker port mapping
on `127.0.0.1:6379`, then host networking on `127.0.0.1:6380`, then the original mapped endpoint.
**All 46 client/method cases have lower host-network wall time than both bridge passes.**
For Respire's 21 investigated cases, host wall time is **46.5–49.3%** of bridge-a wall time.
This is a descriptive range across cases, not a confidence interval. Fixed phase order,
different Redis instances/ports, and the short diagnostic window limit causal attribution.

Selected observations below are **wall μs / process CPU μs per operation**. CPU is summed
across all process threads and can exceed wall time. It includes fixture background clients,
runtime scheduling and the diagnostic adapter, and excludes the separate Redis process.
Subtracting it from wall time would not measure network waiting.

| Case | Bridge-a wall / CPU | Host wall / CPU | Bridge-b wall / CPU |
| --- | ---: | ---: | ---: |
| Respire PING | 80.24 / 155.56 | 38.32 / 81.95 | 80.09 / 157.82 |
| StackExchange PING | 82.38 / 160.01 | 36.75 / 103.51 | 82.66 / 158.65 |
| Respire GET | 81.78 / 159.23 | 39.12 / 83.17 | 80.96 / 158.12 |
| StackExchange GET | 89.20 / 170.68 | 37.92 / 106.24 | 85.74 / 169.10 |
| Respire SET 10KB | 93.74 / 173.07 | 43.59 / 92.46 | 97.95 / 174.50 |
| StackExchange SET 10KB | 107.47 / 183.54 | 43.32 / 122.12 | 100.88 / 183.31 |

Process allocation totals also include the adapter: for example Respire GET is about
**168 B/op** in each plain diagnostic phase, versus **48 B/op** in BDN. Diagnostic totals
must not be published as library allocation regressions. The machine-readable analysis
retains all 184 observations, including the separate, instrumented bridge-profile pass.

The 46 trace summaries contain **1,299,655 samples**: **246,352 Managed** and
**1,053,303 External**, with no missing stacks or lost events and one measurement boundary
pair per trace. External samples include waiting threads; they are not CPU attribution.
For the 21 Respire cases, **73.9–77.4% of Managed samples** contain
`System.Threading.LowLevelLifoSemaphore` frames. This counts inclusive stacks, not a
percentage of request latency. In the GET pair:

| Managed sample count | Respire GET | StackExchange GET |
| --- | ---: | ---: |
| All Managed samples | 5,261 | 5,452 |
| With thread-pool semaphore frames | 3,992 | 4,124 |
| With socket event-loop frames | 602 | 595 |
| With the measured client's networking frames | 411 | 104 |
| With diagnostic adapter frames | 403 | 94 |

The last client row matches `Respire.Networking.` and `StackExchange.Redis.` respectively.
Inclusive counts can overlap; a client frame deep in an adapter continuation does not make
the whole stack client overhead. Native frames can be unresolved even when a stack is
present. The largest Respire GET client stack (284 samples) is in the socket send path,
with completion, the adapter and the next request on the same stack. The sampled source
already batches FIFO completions and delivers drained replies on the receive thread through
`DeliverThenReceiveCore`; the profile does not expose a redundant per-reply dispatch to remove.

The decision is to retain production behavior. Changing global thread-pool spinning,
reordering response delivery, or removing cancellation/ownership checks would not follow
from these samples. Managed sampling does not resolve kernel off-CPU time, packet transit,
or Redis server execution, so it cannot establish an exact CPU/network split. A controlled
same-rig intervention with kernel/server attribution would be needed to justify such a
change. No optimization was made, so there is no claimed before/after speedup requiring a
new run. This completes the issue's documented-limitation outcome while preserving FIFO,
cancellation and ownership semantics.

#### Reproduce and retain this analysis

The <a href={useBaseUrl('benchmarks/2026-10-server-latency/measurements.zip')}>measurement archive</a>
contains raw BDN samples, reports and logs, rig and container metadata, resolved dependencies,
case manifests, observations, assembly hashes and all stack summaries.
The <a href={useBaseUrl('benchmarks/2026-10-server-latency/traces.zip')}>trace archive</a>
retains all 46 original `cpu.nettrace` files. Only the 46 derived `.etlx` indexes are omitted;
the diagnostic tool's `--summarize TRACE REPORT` command regenerates them from the raw traces.
The <a href={useBaseUrl('benchmarks/2026-10-server-latency/manifest.json')}>SHA-256 manifest</a>
records both archives, every retained artifact file, the omitted indexes and analysis files.
Archived bytes were verified against the downloaded artifact; these copies survive Actions
artifact expiry.

Extract both archives into one directory, then use the downloadable
<a href={useBaseUrl('benchmarks/2026-10-server-latency/analyze.py')}>analysis script</a>
with the original 3.4.5 archive linked in the provenance table. Save the manifest beside
the script; it verifies every consumed measurement and stack summary, each expected raw
trace, and the baseline archive against the manifest's SHA-256 hashes before producing output.
It also checks the recorded sizes of the extracted files:

```bash
python analyze.py extracted 38484247008.zip > analysis.json
```

The script checks case coverage, operation normalization, unchanged diagnostic assemblies,
measurement boundaries and trace completeness. It copies BDN's classifications without
changing its statistical test. The checked-in
<a href={useBaseUrl('benchmarks/2026-10-server-latency/analysis.json')}>analysis output</a>
contains all original/repeat means and allocations, diagnostic totals, sample-type counts
and explicitly named inclusive stack markers.

### Concurrent throughput and pipelines

Both methods issue overlapping GETs through the normal multiplexed API, then await every
reply. The x200 case increases pipeline depth; it does not call an explicit batch API.
`OperationsPerInvoke` already divides elapsed time by 50 or 200. These are amortized costs
per completed GET, **not individual request latency**. Approximate throughput below is
`1,000,000 / mean_microseconds`; multiplying by the concurrency again would overstate it.

| StackExchange version | Workload | Respire μs/GET | StackExchange μs/GET | GET/s Respire / StackExchange (approx.) | Allocated Respire / StackExchange | Respire classification |
| --- | --- | --- | --- | --- | --- | --- |
| 3.4.5 | GET x200 pipelined | 1.630 ± 0.0117 | 1.870 ± 0.0870 | 613,497 / 534,759 | 49 B / 289 B | Faster |
| 3.4.5 | GET x50 concurrent | 3.061 ± 0.0253 | 3.439 ± 0.0454 | 326,691 / 290,782 | 52 B / 291 B | Faster |
| 4.0.127-alpha | GET x200 pipelined | 1.373 ± 0.0052 | 1.716 ± 0.0652 | 728,332 / 582,751 | 49 B / 505 B | Faster |
| 4.0.127-alpha | GET x50 concurrent | 3.037 ± 0.0094 | 3.681 ± 0.0644 | 329,272 / 271,665 | 52 B / 501 B | Faster |

### Publish-to-consume pub/sub

These measurements use RESP3 on both clients, one consuming subscriber, and
an 18-byte `Hello, subscriber!` payload. The timed operation awaits publication and consumption
of the message. Respire throws if the subscription ends or reports a delivery gap.
This measures delivery, not just the PUBLISH acknowledgement, and does not cover fan-out.

The latest unprofiled Medium/two-launch runs at
[`21eee745`](https://github.com/thomhurst/Respire/commit/21eee7459edb5cad574710907b154a29e78df9e2)
report:

| StackExchange version | Run | Respire μs | StackExchange μs | Allocated Respire / StackExchange | Respire classification |
| --- | --- | --- | --- | --- | --- |
| 3.4.5 | [38696158383](https://github.com/thomhurst/Respire/actions/runs/38696158383) | 233.9 ± 1.15 | 230.9 ± 1.21 | 400 B / 672 B | Same (ratio 1.01) |
| 4.0.127-alpha | [38696161089](https://github.com/thomhurst/Respire/actions/runs/38696161089) | 124.7 ± 0.75 | 125.7 ± 1.46 | 400 B / 1064 B | Same (ratio 0.99) |

The earlier corrected runs at `7cd38895` remain valid observations on their own rigs:

| StackExchange version | Respire μs | StackExchange μs | Allocated Respire / StackExchange | Respire classification |
| --- | --- | --- | --- | --- |
| 3.4.5 | 225.7 ± 0.89 | 222.9 ± 1.41 | 400 B / 672 B | Same |
| 4.0.127-alpha | 174.0 ± 1.30 | 154.5 ± 3.85 | 399 B / 1046 B | Slower |

The 3.4.5 comparison remains `Same`. The earlier 4.0 `Slower` result (ratio 1.13) used
AMD EPYC 9V74; the new tie used Intel Xeon Platinum 8573C. This does not demonstrate an
improvement or a regression between versions or commits. Lower allocation in all four
runs does not cancel the latency findings. The original full-suite pub/sub reports lack
the timed delivery-gap guard and remain superseded; both tables here retain that guard.

#### Acknowledgement and consumer diagnostics

The follow-up investigation separates time to the PUBLISH acknowledgement from the
remaining wait through consumer completion and UTF-8 decoding, and records how often
the subscriber read is already complete. Delivery can overlap acknowledgement, so
the remaining phase is not an independent measure of delivery latency. Both phases
retain real message consumption and the delivery-gap guard.

Each new run also retained 5,000 samples per client after 500 warmups, alternating client
order, validating payload and recipient count, and recording synchronous read completion.
Times below are **instrumented diagnostic microseconds**, not client performance comparisons:

| Dependency | Client | Ack mean / P95 | After-ack mean / P95 / P99 | Synchronous reads |
| --- | --- | --- | --- | --- |
| 3.4.5 | Respire | 201.174 / 261.489 | 29.779 / 75.855 / 98.534 | 1571 / 5000 |
| 3.4.5 | StackExchange | 208.874 / 271.217 | 28.022 / 75.291 / 99.336 | 1748 / 5000 |
| 4.0.127-alpha | Respire | 128.091 / 163.571 | 16.943 / 39.922 / 54.796 | 2389 / 5000 |
| 4.0.127-alpha | StackExchange | 157.326 / 186.250 | 5.705 / 24.136 / 43.884 | 3850 / 5000 |

The 3.4 residual distributions are similar. Against 4.0, Respire's residual mean is
11.238 μs longer, while its acknowledgement mean is 29.235 μs shorter. A subscriber
has more time to become ready when acknowledgement takes longer: the residual and
synchronous-read differences alone cannot establish slower delivery or a redundant hop.
The probe also adds timestamps, cancellation/deadline handling and validation; its phase
sums must not replace the unprofiled comparison above.

The separate ShortRun EventPipe/threading passes show these paths in both Respire traces:
`PublishAsync`, `FlushLoopAsync`, `ReceiveLoopAsync`, `CompletionScheduler.ExecuteCore`,
`SubscriptionHub.EnqueueMessage`, `SubscriptionBuffer.Write`, the bounded readiness
channel, and `SubscriptionBuffer.ReadAllAsync`. The StackExchange traces include
`RedisSubscriber.PublishAsync`, `ChannelMessageQueue.Write` and `ConnectionMultiplexer.OnMessage`;
the 4.0 trace also includes `PubSub.PublishAsync` and `RespPushDispatch.Deliver`.
This locates the publication, delivery and consumer paths, but does not identify a
removable bottleneck. All four traces also include connection/handshake setup, BDN engine
work, thread-pool waits/spinning, GC-related frames and unresolved `?!?` leaves. Setup
creates both clients even when only one client's method is benchmarked.

The <a href={useBaseUrl('benchmarks/2026-10-pubsub/analysis.json')}>derived analysis</a>
retains exact observed frame names and recalculated phase percentiles. Its stack inventory
uses positive-duration exported intervals ending in `CPU_TIME` to establish frame presence
only. Whole-process inclusive weights, exporter intervals and runtime wait frames must not
be interpreted as per-operation CPU cost, kernel scheduling delay, or Redis server time.
The raw `.nettrace` files remain available for deeper attribution.

Threading diagnostics reported completed work items per operation of **3.8389 / 4.4043**
(Respire / StackExchange 3.4) and **3.6802 / 5.2412** (Respire / StackExchange 4.0).
Lock contentions were **0.0210 / 0.0054** and **0.0034 / 0.0176**, respectively. These
process-wide instrumented counts do not isolate the subscription buffer or the critical
path. Neither a consistent excess of Respire work items nor a consistent contention
disadvantage appears across the two rigs; fewer work items alone cannot establish lower latency.

#### Investigation outcome

[#1745](https://github.com/thomhurst/Respire/issues/1745) concludes with **no safe latency
improvement demonstrated by this evidence**, using its explicit profiling-only outcome.
The code review considered these changes:

| Candidate | Evidence and disposition |
| --- | --- |
| Inline readiness continuations | `SubscriptionBuffer.Write` signals while holding the buffer gate, within the hub's routing gate. Enabling synchronous continuations could run consumer work on the receive path under those gates. The traces and work-item counts do not establish a redundant hop worth that change. |
| Remove buffering or routing synchronization | The hub gate orders epoch checks and enqueueing against reconnect markers. The buffer gate protects bounded messages and ordered/coalesced gaps. Contention counts do not attribute a latency bottleneck to either gate; removing them would require a new correctness design. |
| Skip payload ownership or decoding | The receive payload is copied once for the subscription targets, and the benchmark consumes text. The profiles do not establish copying/decoding as the cause; bypassing ownership or returning before consumption would change the operation being measured. |

These decisions follow the measured-source
[`SubscriptionBuffer`](https://github.com/thomhurst/Respire/blob/21eee7459edb5cad574710907b154a29e78df9e2/src/Respire/PubSub/SubscriptionBuffer.cs)
and [`SubscriptionHub`](https://github.com/thomhurst/Respire/blob/21eee7459edb5cad574710907b154a29e78df9e2/src/Respire/PubSub/SubscriptionHub.cs).
No runtime behavior changed in this conclusion, so there is no new delivery, gap, ordering,
cancellation or disposal behavior to validate. This is not proof that optimization is
impossible. A future candidate needs an isolated attribution experiment, a controlled
before/after comparison on the same rig, and correctness coverage for those contracts.
Fan-out, slow consumers, reconnects and other payload sizes remain outside this measurement.

The `profile-pubsub` collection that produced these diagnostics was removed after the
investigation closed; it remains in [9b570e2c](https://github.com/thomhurst/Respire/commit/9b570e2c).
Only the unprofiled reports support the published latency/allocation comparisons; the
instrumented ShortRun results do not establish a win.

### Client-side cache hits and server reads

With [client-side caching](./fundamentals/client-side-caching), a repeated read can return
from bounded local memory. Redis invalidates tracked entries when keys change. A hot-cache
measurement excludes the network round trip and should not be described as a faster server read.

StackExchange.Redis 4.0.127-alpha has a built-in server-assisted cache. The dedicated
`ClientCacheComparisonBenchmarks` suite compares **local GET hits on both clients**, using
the 4.0 ValueTask API. Setup verifies that warmed reads do not increase Redis's GET counter.
Times here are in **nanoseconds**:

| GET hit | Respire ns | StackExchange 4.0.127-alpha ns | Allocated Respire / StackExchange | Respire classification |
| --- | --- | --- | --- | --- |
| Present key | 114.9 ± 0.15 | 191.0 ± 1.23 | 0 B / 112 B | Faster |
| Missing key | 117.3 ± 0.22 | 135.6 ± 0.29 | 0 B / 0 B | Faster |

The 3.4.5 build has no corresponding built-in-cache comparison. Its absent cache-hit rows
are unavailable scenarios, not failed benchmarks or inferred wins.

The separate `ClientSideCachingBenchmarks` suite deliberately leaves StackExchange caching
disabled, including on 4.0. It compares Respire local hits with two **uncached server-read
controls**. All times in this table are **nanoseconds**, so server reads are roughly 100,000 ns:

| StackExchange version | Operation | Respire local ns | Respire server ns | StackExchange server ns | Respire server classification |
| --- | --- | --- | --- | --- | --- |
| 3.4.5 | EXISTS hot | 269.9 ± 3.57 | 108,891.0 ± 857.81 | 109,232.4 ± 573.16 | Same |
| 3.4.5 | GET hot | 125.8 ± 1.01 | 108,594.1 ± 744.94 | 109,178.1 ± 627.74 | Same |
| 3.4.5 | GET missing hot | 128.0 ± 0.71 | 108,265.8 ± 650.90 | 108,988.4 ± 780.66 | Same |
| 3.4.5 | HGET hot | 321.2 ± 1.38 | 107,937.9 ± 871.69 | 110,662.0 ± 999.26 | Same |
| 4.0.127-alpha | EXISTS hot | 275.4 ± 1.05 | 100,314.6 ± 1,844.13 | 113,988.5 ± 798.58 | Faster |
| 4.0.127-alpha | GET hot | 114.0 ± 0.22 | 101,525.9 ± 1,524.58 | 115,558.1 ± 943.43 | Faster |
| 4.0.127-alpha | GET missing hot | 117.1 ± 0.89 | 97,328.2 ± 3,006.88 | 114,271.2 ± 968.42 | Faster |
| 4.0.127-alpha | HGET hot | 319.8 ± 4.34 | 103,605.2 ± 1,607.10 | 115,272.8 ± 842.33 | Faster |

All Respire local-hit rows in this controls suite are classified `Faster` than its uncached
StackExchange baseline, an expected benefit of avoiding network work. They are not the 4.0
cache-versus-cache results above. Respire local hits reported 0 B in these cases. Full
server-read allocations and variation are retained in the archives. The four 3.4.5 server-read
ties are dispositioned in the [server-latency investigation](#server-latency-investigation-outcome).

### Provenance and preserved evidence

The four original runs used BenchmarkDotNet **0.15.8**, **net10.0**, SDK **10.0.402**, runtime
**10.0.13**, X64 RyuJIT, Medium with **two launches**, **10 warmups** and **15 measurement
iterations** per launch. They ran on GitHub-hosted `ubuntu-latest`, Ubuntu **24.04.5 LTS**,
runner image **ubuntu24 20261004.327.1**, kernel **6.17.0-1022-azure**, with **4 logical /
2 physical cores**. Logs report that raising process priority was denied; the measurements
therefore include the hosted runner's scheduling conditions.

Redis was **8.10.2**, image `mirror.gcr.io/library/redis:8.10`, image ID
`sha256:992eb34c6118138585e0d651b256e0085d1b6aaecf39dc16df5ba50cfbe1609e`, reached at
`localhost:6379` through Docker port mapping without TLS. This is a local container
comparison, not a remote deployment, Cluster, Sentinel, TLS, or .NET 8 measurement.

| Scope | StackExchange.Redis resolved version | Run | CPU | JIT target | Evidence |
| --- | --- | --- | --- | --- | --- |
| Full suite, Oct 13–14 | 3.4.5 | [38484247008](https://github.com/thomhurst/Respire/actions/runs/38484247008) | Intel Xeon Platinum 8573C | x86-64-v4 | <a href={useBaseUrl('benchmarks/2026-10-comparison/38484247008.zip')}>ZIP</a> |
| Full suite, Oct 13–14 | 4.0.127-alpha | [38484250090](https://github.com/thomhurst/Respire/actions/runs/38484250090) | AMD EPYC 9V74 | x86-64-v4 | <a href={useBaseUrl('benchmarks/2026-10-comparison/38484250090.zip')}>ZIP</a> |
| Corrected pub/sub, Oct 15 | 3.4.5 | [38664941232](https://github.com/thomhurst/Respire/actions/runs/38664941232) | AMD EPYC 7763 | x86-64-v3 | <a href={useBaseUrl('benchmarks/2026-10-comparison/38664941232.zip')}>ZIP</a> |
| Corrected pub/sub, Oct 15 | 4.0.127-alpha | [38664943357](https://github.com/thomhurst/Respire/actions/runs/38664943357) | AMD EPYC 9V74 | x86-64-v3 | <a href={useBaseUrl('benchmarks/2026-10-comparison/38664943357.zip')}>ZIP</a> |
| Pub/sub follow-up, Oct 15 | 3.4.5 | [38696158383](https://github.com/thomhurst/Respire/actions/runs/38696158383) | AMD EPYC 7763 | x86-64-v3 | <a href={useBaseUrl('benchmarks/2026-10-pubsub/38696158383.zip')}>ZIP</a> |
| Pub/sub follow-up, Oct 15 | 4.0.127-alpha | [38696161089](https://github.com/thomhurst/Respire/actions/runs/38696161089) | Intel Xeon Platinum 8573C | x86-64-v4 | <a href={useBaseUrl('benchmarks/2026-10-pubsub/38696161089.zip')}>ZIP</a> |

The two pub/sub follow-ups used source `21eee7459edb5cad574710907b154a29e78df9e2` from
[#1748](https://github.com/thomhurst/Respire/pull/1748), the same BDN/SDK/runtime versions,
Medium settings, Redis image ID and port-mapped endpoint above. Their runner images were
**ubuntu24 20261012.334.1** (3.4.5) and **ubuntu24 20261004.327.1** (4.0), both Ubuntu
24.04.5 with kernel 6.17.0-1022-azure and 4 logical / 2 physical cores. Each completed
two unprofiled comparison cases and two separately instrumented ShortRun cases. Both
unprofiled comparisons classify Respire as `Same`; instrumentation is excluded from those tables.

The ordinary/cache results used Respire source commit
[`17f617e3da1fef7e5317246db26b081be02f468f`](https://github.com/thomhurst/Respire/tree/17f617e3da1fef7e5317246db26b081be02f468f/benchmarks/Respire.ComparisonBenchmarks)
from preparation PR [#1627](https://github.com/thomhurst/Respire/pull/1627).
The only subsequent comparison-benchmark source change before that PR merged was the
pub/sub delivery-gap guard; the other measurements remain attributed to their original commit.
Corrected pub/sub used
[`7cd38895b99f4f4738caf9cd54275dee94d6d7a7`](https://github.com/thomhurst/Respire/tree/7cd38895b99f4f4738caf9cd54275dee94d6d7a7/benchmarks/Respire.ComparisonBenchmarks).
Respire was built from source (project version 1.0.0), not a published-package comparison.
The full runs used filter `*`; replacements used `*PubSubComparisonBenchmarks*`.

Artifact validation confirmed 52 and 56 completed cases in the full runs and two in each
replacement, with populated statistics and no failed benchmarks. Of the full-run cases,
two pub/sub cases per version are excluded from publication in favor of the corrected runs.
The 3.4.5 build excludes the four 4.0-only local cache cases by design. No failed or unavailable
scenario is represented as a win.

The ZIPs are checked into the website and preserve every file from the downloaded artifacts:
raw benchmark logs, JSON measurement reports, CSV/Markdown/HTML summaries, `rig.txt`, and
resolved `project.assets.json`. Their bytes were verified after archiving.
The <a href={useBaseUrl('benchmarks/2026-10-comparison/manifest.json')}>SHA-256 manifest</a>
records archive and individual-file hashes, commits, versions, and run links. These copies
remain available after the original Actions artifacts' 90-day retention expires.

The pub/sub follow-up ZIPs additionally preserve phase samples and summaries, diagnostic
logs, both clients' EventPipe `.nettrace` and speedscope files, and instrumented reports:
**22 byte-verified files per run**. Their separate
<a href={useBaseUrl('benchmarks/2026-10-pubsub/manifest.json')}>SHA-256 manifest</a> records
every archived file. Download both ZIPs, that manifest and
<a href={useBaseUrl('benchmarks/2026-10-pubsub/analyze.py')}>analyze.py</a> into one directory,
then run `python analyze.py` to verify the hashes and reproduce `analysis.json` without
running benchmarks. The archives retain full unprofiled distributions, allocations and
rig/source provenance as well as the diagnostic evidence.

This completes the measured publication scope of
[#1625](https://github.com/thomhurst/Respire/issues/1625) and the pub/sub investigation
[#1745](https://github.com/thomhurst/Respire/issues/1745). The server-operation latency
investigation [#1744](https://github.com/thomhurst/Respire/issues/1744) is dispositioned above.
Publication does not establish a universal performance advantage.

## Automatic pipelining

Concurrent calls share socket writes automatically. No batching mode is needed:

```csharp
await Task.WhenAll(
    redis.GetStringAsync("a").AsTask(),
    redis.GetStringAsync("b").AsTask(),
    redis.IncrementAsync("hits").AsTask());
```

## Connection count tuning

The default is one multiplexed connection, which usually gives the best throughput. If
profiling shows large concurrent responses saturating one socket, benchmark two:

```csharp
var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost", 6379) },
    Connections = 2,
});
```

More connections split commands into smaller batches and add overhead. Raise this only when
measurements show one connection is the bottleneck.

## When explicit batches help

Use `CreateBatch` to queue commands one by one and send them in one flush. See [batches and transactions](./guides/batches-and-transactions).

## Buffer ownership

Normal APIs return managed strings or arrays. For large payloads on measured hot paths, [leased reads](./fundamentals/values-and-serialization#zero-copy-leased-reads) return pooled memory that you must dispose.

## Dedicated blocking pool

`BLPOP`, `BLMOVE`, and blocking stream reads use a dedicated connection pool, so they do not delay other commands.

## Measure your workload

`benchmarks/Respire.Benchmarks` covers protocol, pipeline, throughput, allocation, and
container scenarios, some against StackExchange.Redis. Run it on net8.0 and net10.0:

```bash
bash ./run-benchmarks.sh -t All
```

The published comparison comes from `benchmarks/Respire.ComparisonBenchmarks`. It uses
`REDIS_HOST`/`REDIS_PORT` when set, otherwise a throwaway Testcontainer:

```bash
dotnet run -c Release -f net10.0 --project benchmarks/Respire.ComparisonBenchmarks -- --filter '*'
```

CI publishes [comparison benchmarks](./benchmarks) and [stress tests](./stress-tests) weekly. Treat them as a starting point, then profile your own payloads, concurrency, server, network, and serializer. Sources are in [`benchmarks`](https://github.com/thomhurst/Respire/tree/main/benchmarks); transport details are in [transport write evidence](./guides/transport-write-evidence).
