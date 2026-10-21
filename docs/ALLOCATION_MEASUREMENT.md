# Allocation measurement boundaries

Every zero-allocation test must measure inside `AllocationMeasurement.WithoutConcurrentGc`.
A bare `GC.GetAllocatedBytesForCurrentThread()` delta is flaky on CI: concurrent GC,
thread-pool growth and runtime caches charge bytes to the measuring thread.

## Rules

1. Mark the test unkeyed `[NotInParallel]`. The no-GC region is process-wide, and unkeyed
   `NotInParallel` excludes all other TUnit tests.
2. Warm every measured path before opening the region.
3. Read the counters inside a synchronous `[MethodImpl(MethodImplOptions.NoInlining)]`
   method. Keep assertions, delegate construction and result storage outside it. No
   `await` inside the region.
4. Add a positive control: run the same loop with one escaping `new byte[37]` per
   iteration and require at least `37 * iterations` bytes. This proves the boundary still
   observes allocations.
5. Keep the exact assertion. Do not retry, discard samples, add a tolerance or rerun until
   green.
6. Keep total region allocations well below the 16 MiB reservation. The reservation is a GC
   budget, not an allowed allocation threshold.

`WithoutConcurrentGc` throws if it cannot start the region. If the region is not retained,
it wraps the `GC.EndNoGCRegion` failure, unless the measurement itself threw first; that
exception propagates unchanged.

A lost-region failure reports the 16 MiB reservation and separate preparation and
measurement snapshots: process and measuring-thread allocation deltas, generation 0/1/2
collection deltas, latency-mode transitions, and elapsed time. Preparation includes the
finalizer wait and cast-cache primer. Snapshot counters are outside the measured methods;
diagnostic strings are built only after region cleanup fails. These diagnostics do not
retry the sample or permit a nonzero allocation result.

The first snapshot runs after `TryStartNoGCRegion` returns; entry and capture are not
atomic. An interruption in that gap can therefore produce `Interactive->Interactive`
with zero collection deltas in both intervals. `CollectionBeforeInitialSnapshotIsStillRejected`
forces a collection in that gap using the helper's optional lifecycle callback and verifies
that cleanup still rejects the sample. The initial snapshot is inside the cleanup boundary,
so an exception during entry diagnostics also releases the region. This control explains
how that diagnostic shape is possible; it does not identify what interrupted the historical
Sentinel CI sample (#2098).

Compare process bytes with thread bytes to identify allocation outside the measured
thread. A collection or mode change during preparation points to finalizer/runtime or
other process activity before the measured delegate. The cleanup exception alone does
not identify the cause: .NET can report `NoGCRegion mode must be set` after an interrupted
region [clears its bookkeeping](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/gc/gc.cpp#L20844-L20855).
Preserve the failure and use runtime GC events to
distinguish an induced collection from budget exhaustion when the counters are ambiguous.

### When to use a child process

Use a bounded child process when the measured path depends on process-wide state that
warmup cannot fix:

- **Thread-pool growth.** Queuing work can create a pool worker on the signaling thread
  (136 bytes on x64: a 72-byte `Thread` plus a 64-byte `Thread+StartHelper`), even inside a
  no-GC region. The dispatched-wake test runs a child with pool minimum and maximum both 2,
  both workers started before measurement, and fails if the worker count changes.
- **Tiered compilation.** Tier-0 code can box struct commands through default interface
  methods. The standalone batch start test runs its child with `DOTNET_TieredCompilation=0`.
- **Shared pools and thread statics.** Rent and return `ArrayPool.Shared` arrays and create
  thread-static scratch buffers on the measuring thread before the counter starts.
- **Runner process activity.** `NotInParallel` excludes test bodies, but the runner,
  collector and leftover background work still share the process-wide GC reservation.
  The 10,000-owner dispatch burst uses a fresh child before runner startup. A handshake
  regression forces a parent collection or allocates 64 MiB in the parent while the
  child's region is open; separate controls require the child to reject an allocating
  ordinary path and a lost region. `BackgroundAllocationsExhaustTheProcessWideRegion`
  reproduces budget loss using only small-object allocations on a different thread,
  without an explicit collection. An unkeyed test does not give the measuring thread
  exclusive use of that budget.
  The original CI failure did not record a GC reason, so this isolation does not establish
  which activity interrupted that historical sample. These deterministic regressions
  verify the isolation fix independently of the unrecoverable historical GC reason (#2066).
  Sentinel normalized-evidence matching also runs before runner startup in a bounded child.
  Its handshake control forces a parent collection while the child region is open, and its
  failure controls inject an ordinary-path allocation and an induced collection in the child.
  Both remain failures; no region loss is ignored or retried (#2098).

Child-process rules:

- The parent stays an unkeyed `NotInParallel` test and leaves the runner's pool settings
  unchanged.
- The child enters through an explicit environment mode from the normal entry point, before
  the test runner starts. It reuses the current apphost or .NET host (`DOTNET_HOST_PATH`
  fallback) and checks `Environment.Version` against the parent (a coarse guard only).
- Normal execution delegates to `MicrosoftTestingPlatformApplication.RunAsync`, generated by
  the pinned TUnit package. Keep that and the entry point in sync when upgrading TUnit. The
  `TUnit0034` suppression stays local to `Main`.
- Bound startup, warmup, completion, exit and cleanup. Child startup and execution get 30
  seconds; post-kill waits and pipe cleanup get five seconds each. Failure output separates
  stdout and stderr with an explicit newline.
- Preserve the environment and child coverage configuration under a coverage collector.

The circuit-disabled dispatch probe writes and flushes a bounded set of stage records to
stderr: managed entry, pool warmup, connection/command warmup, dispatch/control warmup,
no-GC preparation, each measurement, and client/server disposal. Records include runtime,
dispatch/retry modes, elapsed time and worker state. If it must be killed, the last record
identifies the operation it entered; no entry record means managed entry is unconfirmed,
not that measurement timed out. Command-receipt and completion timeouts also name the
iteration and ownership state. Stage formatting stays outside the dispatch byte counters.

This probe runs its synchronous dispatch/wait loop on the child entry thread, leaving
both warmed pool workers available for socket and flush work. Its busy-worker control
holds one worker throughout measurement: all four dispatch/retry combinations stalled
in command receipt with the previous async continuation, and complete with the entry-thread
measurement. This establishes a starvation path, not the cause of the historical empty-output
30-second Windows failure in #2107. The production probe's 30-second deadline, two-worker
limit, no-GC boundary, zero-byte assertion and allocation positive control are unchanged.
Killed-child controls stop at entry, pool warmup, measurement and disposal to check that
stage diagnostics survive process termination; their short exit deadline begins after a
readiness handshake and does not change the ordinary probe's deadline.

## Diagnosing a nonzero sample

1. Keep the failing run. Do not rerun until green or weaken the assertion.
2. Report per-caller bytes, the first allocating iteration, and generation 0/1/2
   `GC.CollectionCount` deltas around the interval (outside the counted work).
3. A delta on the first iteration right after the region's GC points at runtime state the GC
   reset. A late iteration from one caller points at that caller's path.
4. Capture allocation stacks with a startup EventPipe session. In-process `EventListener`
   sessions get no per-object allocation events, because sampling must be enabled at
   runtime start:

   ```text
   DOTNET_EnableEventPipe=1
   DOTNET_EventPipeConfig=Microsoft-Windows-DotNETRuntime:0x1210001:5
   ```

The dispatched-wake child records cumulative bytes and `ThreadPool.ThreadCount` after every
wake into preallocated arrays, and runs a `DispatchedWakeEventLog` listener on
`Microsoft-Windows-DotNETRuntime`. A failure lists the allocating wakes, worker counts, pool
work items, runtime events in the interval and eight fresh samples. The failing sample still
decides the result. `DispatchedWakeFailureReportsAllocatingWakeAndRuntimeEvents` checks this
report.

## Known runtime effects

| Effect | Bytes | How the boundary handles it |
| --- | --- | --- |
| Concurrent GC bookkeeping charged to the measuring thread ([dotnet/runtime#134724](https://github.com/dotnet/runtime/issues/134724)) | Thousands, even for empty intervals | The no-GC region excludes concurrent GC. |
| Surrounding allocations inside the JIT's measurement boundary ([dotnet/runtime#96836](https://github.com/dotnet/runtime/issues/96836)) | Varies | Measure in a no-inline method. |
| .NET 8 cast cache table (`CastHelpers.s_table`) reallocated after the region's full GC flushes it | 24,624, 49,200 or 98,352 (grows with distinct cast pairs seen by the process) | `WithoutConcurrentGc` calls `GC.WaitForPendingFinalizers()`, then a failing `object is object[]` cast, before `measure`. |
| Thread-pool worker start on the queuing thread | 136 per worker on x64 | Child process with a fixed two-worker pool. |
| Tier-0 boxing through default interface methods | 136 per call for `IncrementCommand` | Child process with `DOTNET_TieredCompilation=0`. |
| `ArrayPool.Shared` miss (per-thread slot and per-core stacks can be trimmed by gen-2 GCs) | Array size | Rent and return on the measuring thread before measuring. |
| First command serialized on a thread (thread-static 4 KiB scratch buffer) | 4,216 | Create it on the measuring thread before measuring. |

The cast cache flush happens on the finalizer thread after the GC, so a priming cast without
the finalizer wait can still miss. Shared canonical generic code (for example
`DispatchResponseSource<string?>.Run`) hits the cast cache; `int` instantiations do not.

## Unknown-slot Cluster replica discovery

`ClusterReplicaDiscoveryTests` compares per-slot `ConcurrentDictionary<int, ClusterReplicaSet>`
coalescers with the shared coordinator for 256 persistently uncovered slots (synchronous
bookkeeping only; no network I/O or topology parsing). Windows x64 Release:

| Runtime | Per-slot coalescers | Shared coordinator |
| --- | ---: | ---: |
| .NET 8.0.31 | 73,664 bytes | 55,288 bytes |
| .NET 10.0.12 | 73,672 bytes | 55,288 bytes |

The coordinator figure includes per-slot attempt versions and the current probe slot.
Coordinator behavior the tests pin:

- With a full-coverage reply held until 256 callers join, the old coalescers start 256 probes;
  the coordinator starts one.
- A waiting slot the first reply did not cover gets a second probe. N distinct, unattempted,
  still-uncovered slots need N sequential probe rounds.
- Uncovered slots keep independent one-second throttles. A completed, throttled slot returns
  its cached attempt at once; a caller for the pending probe's own slot joins that probe.
- MOVED owner changes and SMIGRATED invalidate the old discovery attempt.
- Caller cancellation detaches only that caller; router disposal cancels the physical probe.

`CoveredSlotsDoNotAllocate` checks 1,000 covered lookups with the same boundary and a positive
control. Healthy replica selection bypasses the coordinator.
