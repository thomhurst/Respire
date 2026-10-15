---
title: Performance
description: Measured Linux comparisons with StackExchange.Redis 3.4.5 and 4.0.127-alpha, including limitations and reproducible evidence.
---

import useBaseUrl from '@docusaurus/useBaseUrl';

# Performance

In these Linux Redis-container measurements, Respire is faster for concurrent and pipelined
GETs against both StackExchange.Redis versions. Most single-operation timings against 3.4.5
show no established latency advantage. Against 4.0.127-alpha, the ordinary-operation and
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
All 17 ordinary-operation ties against 3.4.5 are tracked in
[#1744](https://github.com/thomhurst/Respire/issues/1744), including SET 10KB.
The older [10KB SET investigation](https://github.com/thomhurst/Respire/issues/1553) concerned
a different, noisy run; these measurements do not establish a continuing 15% regression.

The comparison workflow has an opt-in `server-latency-diagnostics` dispatch input for
that investigation. It runs the unchanged CommonOperationsBenchmarks methods and the
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
to investigate, but cannot alone prove a scheduler cause. No optimization or new latency
win is established by adding this collection path. The 21 ties remain open until the
hosted evidence is analyzed and any candidate change receives a before/after comparison.

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

Manual comparison runs can enable `profile-pubsub` to reproduce the diagnostic collection.
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
ties are also explicitly covered by [#1744](https://github.com/thomhurst/Respire/issues/1744).

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
follow-up is tracked separately in [#1744](https://github.com/thomhurst/Respire/issues/1744).
It does not establish a universal performance advantage.

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
