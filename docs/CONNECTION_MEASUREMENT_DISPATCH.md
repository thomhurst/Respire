# Connection measurement dispatch: point-in-time investigation

This investigation addresses #1063 against revision
`69ea7200ff05719852f5bfdd3512b7e2c4041a22`. At this revision, lifecycle delivery
already reserves a process-wide maximum of 64 queued or running callbacks. This
bound predates the investigation.
The hashes, runtime versions, and workstation measurements below record this
investigation; they are not continuously maintained performance baselines.

The decision is to retain the existing bounded work-item dispatcher. A single
Channel reader improves the fast-callback model but fails independent delivery
when a listener blocks. Four readers also fail that contract and slow fast
delivery. Sixty-four readers preserve the isolation probe but make fast delivery
4.34–4.62 times slower and increase allocation 3.16–3.72 times in the model.
Their slow-callback timing intervals overlap the existing dispatcher on both
runtimes. These measurements do not justify replacing the bounded implementation.

## Workloads and measurement boundaries

Measurements use .NET SDK 10.0.401, BenchmarkDotNet 0.15.8, Windows 11 x64,
an Intel Core i7-12700K, and concurrent workstation GC. The runtime versions are
.NET 8.0.31 and .NET 10.0.12. These are local investigation results on a shared
workstation, rather than a CI performance gate or a guarantee for other hosts.
The Redis image used is
`redis@sha256:29e8589c3f9ba699b5f7aa4b3c7733c58852a3626439e619aa0ee78de08c6ca0`
under the local `redis:7.2-alpine` tag.

Failed Docker/Redis runs, dry runs, and an acquisition diagnostic with a retained
saturation listener are excluded. Final rental cases use separate processes and
verified-unused Redis/reaper ports, with complete statistics and drained-delivery
records. The [published evidence](https://github.com/thomhurst/Respire/issues/1063#issuecomment-6015088702)
retains the detailed failure, port-selection, and exclusion history.

The task-scoped fixture is separate from the permanent benchmark suite. Its
dispatch and rental methods are identical across runtime builds. BenchmarkDotNet's
in-process toolchain avoids project-name ambiguity with the repository's existing
`Respire.Benchmarks` friend assembly. Each runtime starts in a separate guarded
process. Dispatcher model cases share their runtime's process and thread pool,
with independent meters. Each final rental case starts in a fresh process to
prevent failed setup or cleanup from contaminating subsequent listeners. Dry
runs validate execution and are excluded from the performance tables. Final
timings use the default job; errors are half-widths of the 99.9% confidence
intervals, not standard errors.

Three complementary workloads distinguish dispatch cost from transport cost:

* Actual connection wait/handoff bursts call the existing internal instrumentation
  on a connected dedicated transport. Every burst submits 32 events and waits for
  all 32 deliveries, with no drops. Reported cost includes delivery coordination.
* Dispatcher comparisons submit the same 32 histogram samples, callbacks, tags,
  and typed payload to work items or bounded Channels with 1, 4, or 64 readers.
  All implementations reserve 64 queued-plus-running entries, reject new entries
  at capacity, isolate callback exceptions, and suppress synchronous continuation
  execution on the producer. Every measured event must deliver. This model adds
  an owner reference to the payload so each implementation has independent
  counters; its allocation totals are distinct from actual library measurements.
* Dedicated rental benchmarks use a real Redis 7.2 Alpine container, RESP2, and the
  actual pool. Warm idle reuse excludes connection establishment. New-connection
  churn disables idle reuse and includes acquisition plus awaited discard/cleanup.
  Container setup, initial connection setup, and final disposal are outside timing.

The slow callback requests `Thread.Sleep(1)`. Its observed duration also depends
on operating-system scheduling and thread-pool availability. The configuration
does not establish an exact one-millisecond service time.

Separate diagnostics time only `RentAsync` for 1,000 new rentals per listener
mode, after 20 warm rentals. Discard happens outside each acquisition timer.
Process allocation totals include transport, producer, background worker, and
listener work through the final delivery drain. They are diagnostic totals,
without zero-allocation assertions or confidence intervals. Event throughput for
these diagnostics includes churn and draining; it is not maximum dispatcher
throughput. Drops are reported alongside delivery counts.

## Actual lifecycle delivery

| Runtime | Event | Mean per event | 99.9% error | Allocated per event |
|---|---|---:|---:|---:|
| .NET 8 | Wait | 366.4 ns | 4.35 ns | 56 B |
| .NET 8 | Handoff | 291.6 ns | 1.04 ns | 40 B |
| .NET 10 | Wait | 350.8 ns | 3.43 ns | 56 B |
| .NET 10 | Handoff | 281.7 ns | 3.27 ns | 40 B |

The measured allocation comes from enabled delivery. The generic
`UnsafeQueueUserWorkItem` call passes its value tuple as typed state; tuple boxing
is not assumed. Disabled-listener contracts are verified independently with
warmed no-inline methods, an unkeyed `[NotInParallel]`,
`AllocationMeasurement.WithoutConcurrentGc`, and a positive allocation control.

## Bounded alternatives

| Runtime | Callback | Dispatcher | Mean per event | 99.9% error | Allocated | Delivered events/s |
|---|---|---|---:|---:|---:|---:|
| .NET 8 | Fast | Work items | 307.7 ns | 3.22 ns | 64 B | 3,250,313 |
| .NET 8 | Fast | Channel, 1 reader | 166.2 ns | 1.79 ns | 0 B reported | 6,015,781 |
| .NET 8 | Fast | Channel, 4 readers | 631.1 ns | 5.39 ns | 38 B | 1,584,485 |
| .NET 8 | Fast | Channel, 64 readers | 1,334.4 ns | 26.50 ns | 202 B | 749,424 |
| .NET 8 | Slow | Work items | 885.630 us | 14.840 us | 64 B | 1,129 |
| .NET 8 | Slow | Channel, 1 reader | 14,137.102 us | 281.014 us | 0 B reported | 71 |
| .NET 8 | Slow | Channel, 4 readers | 3,552.738 us | 69.297 us | 16 B | 281 |
| .NET 8 | Slow | Channel, 64 readers | 879.702 us | 16.664 us | 235 B | 1,137 |
| .NET 10 | Fast | Work items | 283.5 ns | 3.77 ns | 64 B | 3,527,420 |
| .NET 10 | Fast | Channel, 1 reader | 113.7 ns | 0.44 ns | 0 B reported | 8,791,888 |
| .NET 10 | Fast | Channel, 4 readers | 515.6 ns | 10.25 ns | 33 B | 1,939,601 |
| .NET 10 | Fast | Channel, 64 readers | 1,310.4 ns | 21.73 ns | 238 B | 763,121 |
| .NET 10 | Slow | Work items | 863.445 us | 11.456 us | 67 B | 1,158 |
| .NET 10 | Slow | Channel, 1 reader | 13,947.486 us | 270.208 us | 63 B | 72 |
| .NET 10 | Slow | Channel, 4 readers | 3,509.007 us | 66.877 us | 17 B | 285 |
| .NET 10 | Slow | Channel, 64 readers | 873.295 us | 17.195 us | 192 B | 1,145 |

Throughput is calculated from the mean time per delivered event. All comparison
rows deliver every event, so rejecting more work cannot create an apparent win.
Allocation columns include measurement/coordinator effects; the reported zero
for a batched Channel is not a strict allocation contract.

The isolation probe blocks one callback for the one-reader Channel and four for
the other implementations, then offers an unrelated pool's event below capacity.
Work items and the 64-reader Channel deliver the unrelated event before release.
The one- and four-reader Channels leave it queued until release. All probes drain
after release with zero drops. The committed regression test gives the production
dispatcher four blocked callbacks and verifies independent delivery from another
pool.

## Dedicated rental churn

| Runtime | Listener | Workload | Mean | 99.9% error | Allocated per rental |
|---|---|---|---:|---:|---:|
| .NET 8 | Disabled | Warm idle | 42.27 ns | 0.866 ns | 0 B reported |
| .NET 8 | Fast | Warm idle | 41.19 ns | 0.269 ns | 0 B reported |
| .NET 8 | Slow | Warm idle | 41.30 ns | 0.362 ns | 0 B reported |
| .NET 8 | Disabled | New connection and cleanup | 0.9739 ms | 0.06260 ms | 342,348 B |
| .NET 8 | Fast | New connection and cleanup | 0.7804 ms | 0.04321 ms | 342,367 B |
| .NET 8 | Slow | New connection and cleanup | 1.455 ms | 0.1172 ms | 342,638 B |
| .NET 10 | Disabled | Warm idle | 35.94 ns | 0.612 ns | 0 B reported |
| .NET 10 | Fast | Warm idle | 36.73 ns | 0.458 ns | 0 B reported |
| .NET 10 | Slow | Warm idle | 36.84 ns | 0.741 ns | 0 B reported |
| .NET 10 | Disabled | New connection and cleanup | 2.332 ms | 0.2066 ms | 342,266 B |
| .NET 10 | Fast | New connection and cleanup | 1.763 ms | 0.1458 ms | 342,317 B |
| .NET 10 | Slow | New connection and cleanup | 2.860 ms | 0.2073 ms | 342,363 B |

Healthy idle rentals do not queue lifecycle measurements. New rentals produce
creation, wait, and close events. Total rental allocations include the transport
and background work; subtracting separate network runs does not establish the
marginal metric allocation. Direct event measurements above establish dispatch
cost separately. The .NET 8 fast/slow cases delivered 344,068/161,264 measurements,
with 0/6,164 drops, including calibration and final cleanup outside timed iterations.
The .NET 10 fast/slow cases deliver 340,996/83,716 measurements with zero drops.
Warm enabled cases deliver one final close during cleanup and no rental events.
No causal acquisition improvement or runtime comparison is inferred from these
separate network runs on a shared workstation.

| Runtime | Listener | Acquisition median | Acquisition p95 | Delivered / offered | Dropped | Delivered events/s |
|---|---|---:|---:|---:|---:|---:|
| .NET 8 | Disabled | 357.5 us | 640.7 us | 0 / 0 enabled | 0 | 0 |
| .NET 8 | Fast | 405.8 us | 1,071.1 us | 3,000 / 3,000 | 0 | 2,939 |
| .NET 8 | Slow | 757.8 us | 7,780.2 us | 2,957 / 3,000 | 43 | 1,259 |
| .NET 10 | Disabled | 381.5 us | 890.7 us | 0 / 0 enabled | 0 | 0 |
| .NET 10 | Fast | 537.3 us | 1,248.0 us | 3,000 / 3,000 | 0 | 2,199 |
| .NET 10 | Slow | 540.2 us | 11,017.0 us | 2,976 / 3,000 | 24 | 1,419 |

The short diagnostic's total process allocations for disabled, fast, and slow
listeners are 343,708.928, 343,708.568, and 343,529.656 B/rental on .NET 8;
343,896.640, 343,217.624, and 343,490.736 B/rental on .NET 10.
These totals include background scheduling differences and do not isolate the
marginal metric allocation.

## Saturation, memory, and disposal

The actual wait-event diagnostic holds callbacks while increasing offered events
from 64 to one million. Pool disposal completes before callbacks are released.
Groups are disabled before disposal; all 64 accepted events still deliver after
release because group selection and duration are captured when the event occurs.

| Runtime | Offered | Pending | Dropped | Live managed bytes after GC | Process working set |
|---|---:|---:|---:|---:|---:|
| .NET 8 | 64 | 64 | 0 | 2,918,704 | 77,848,576 B |
| .NET 8 | 1,000 | 64 | 936 | 2,931,368 | 77,594,624 B |
| .NET 8 | 100,000 | 64 | 99,936 | 2,931,360 | 77,733,888 B |
| .NET 8 | 1,000,000 | 64 | 999,936 | 2,931,416 | 77,733,888 B |
| .NET 10 | 64 | 64 | 0 | 2,874,456 | 78,471,168 B |
| .NET 10 | 1,000 | 64 | 936 | 2,886,376 | 78,209,024 B |
| .NET 10 | 100,000 | 64 | 99,936 | 2,886,488 | 78,475,264 B |
| .NET 10 | 1,000,000 | 64 | 999,936 | 2,886,544 | 78,475,264 B |

Disposal takes 6.0401 ms on .NET 8 and 8.6195 ms on .NET 10, closing the transport
while 64 deliveries remain pending. Release produces 64 deliveries and zero
pending entries. These short-burst snapshots measure process memory, not exclusively queue memory or a
long-term native-thread plateau. The bound is on retained entries; exceptions and
other referenced object graphs can vary in size.

## Existing notification infrastructure and delivery contract

`ClusterRouter.MigratedNotifications.cs` has a per-router Channel with 128 queued
slots, one reader, and `DropOldest`. A running item sits outside that queue budget.
Its payload is a notification record class,
processing has topology/FIFO requirements, and router disposal joins its worker.
That ownership and shutdown model differs from process-wide lifecycle metrics.
Reusing its single drain would introduce the isolation failure demonstrated above;
reusing its drop mode would also change which lifecycle event is lost. The useful
shared principles are bounded capacity, callbacks outside transport locks,
asynchronous continuations, and isolated listener exceptions.

The lifecycle contract remains explicit:

* Capacity is 64 queued plus running measurements across all clients and pools.
  Saturation rejects the newest attempt without waiting or allocating another
  work item. Enqueue failures release the reservation and count as drops.
* Capacity returns after the callback finishes or throws. A blocked listener can
  consume shared capacity; accepted callbacks can run independently until it is
  exhausted. The observable drop counter remains available during saturation.
* Group selection, durations, close reasons, handoff ownership, and live
  membership transitions are captured synchronously before asynchronous delivery.
  Pool identities retain the existing 64-name budget and bounded overflow identities.
* Events are best effort and can arrive out of order. There is no FIFO promise,
  replay, or flush on client disposal. Queued events can be lost at process shutdown;
  listeners must stay enabled to collect them. Transport cleanup, pool disposal,
  and retirement do not join blocked metric callbacks.

## Reproduction and validation

The repository alone cannot reproduce these measurements: download the nine
temporary fixture files and verify their SHA-256 source hashes from
[issue #1063](https://github.com/thomhurst/Respire/issues/1063#issuecomment-6015063619).
The [complete 32-case Markdown reports and corrected diagnostics](https://github.com/thomhurst/Respire/issues/1063#issuecomment-6015088702)
include delivery records, measured binary hashes, and excluded-run provenance.
Original observations and confidence intervals are published for
[.NET 8](https://github.com/thomhurst/Respire/issues/1063#issuecomment-6015189477) and
[.NET 10](https://github.com/thomhurst/Respire/issues/1063#issuecomment-6015189975),
with hashes of their complete BenchmarkDotNet JSON reports.
Save those files under `artifacts/issue-1063-investigation` at the measured
revision. The runner's path and port parameters were added after measurement for
portability; its C# benchmark methods and default port assignments are unchanged.
Run from the repository root, with Docker available and the selected ports free:

```powershell
& ./artifacts/issue-1063-investigation/Run-IsolatedRentals.ps1 -Framework net8.0 -Evidence C:/temp/lifecycle-results
& ./artifacts/issue-1063-investigation/Run-IsolatedRentals.ps1 -Framework net10.0 -Evidence C:/temp/lifecycle-results

# Run each runtime's dispatcher comparison in its own guarded process.
& ./scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'artifacts/issue-1063-investigation/bin/Release/net8.0/Respire.Benchmarks.dll',
    '--filter', '*DispatchBenchmarks.CurrentWorkItems*', '*DispatchBenchmarks.Channel*',
    '--inProcess', '--noOverwrite', '--exporters', 'json')

# Set unused Redis/reaper ports before each separate actual-event/diagnostic process.
$env:RESP_LIFECYCLE_REDIS_PORT = '26363'
$env:RESP_LIFECYCLE_REAPER_PORT = '26520'
& ./scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'artifacts/issue-1063-investigation/bin/Release/net8.0/Respire.Benchmarks.dll',
    'diagnostics', 'C:/temp/lifecycle-results/diagnostics-net8.log')
```

Repeat the dispatcher/diagnostic commands using the `net10.0` DLL. For actual
wait or handoff delivery, replace the benchmark filter with
`*ActualWaitDispatchBenchmarks*` or `*ActualHandoffDispatchBenchmarks*`, one case
per process. Allow reaper cleanup before reusing its port or select a different
unused port. The .NET 10 wait result predates explicit port selection; only the
untimed Redis construction differs from the published fixture.

The added coverage verifies delivery below capacity with four blocked callbacks,
capacity recovery after throwing callbacks, delivery after group changes while
an isolated worker gate guarantees the callback has not yet started,
and disabled-listener allocation with both None and All groups. Existing focused
tests cover acquisition, retirement, transport cleanup, pool disposal, close and
handoff ownership, bounded identities, and disabled command allocations. No
production dispatcher or ordering/loss contract changes.
