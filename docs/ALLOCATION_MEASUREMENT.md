# Allocation measurement boundaries

`KnownLayoutValidationAllocatesNothingAfterInitialization` requires exactly zero
managed bytes for 1,000 initialized `KEYDB.MEXISTS` validations. Its production
path and zero-byte expectation are unchanged.

## Failure evidence

Issue [#521](https://github.com/thomhurst/Respire/issues/521) records two failures:

- [net10.0 with coverage](https://github.com/thomhurst/Respire/actions/runs/36682580105/job/109781095259): 4,712 bytes.
- [net8.0 without coverage](https://github.com/thomhurst/Respire/actions/runs/36683998527/job/109785554139): 6,568 bytes.

Coverage is therefore not necessary for the failure. The first run's coverage
artifact includes the Respire assembly, not the test assembly. Excluding the test
from coverage would not correct this measurement.

The ASCII hash-tag path performs a frozen dictionary lookup and CRC calculation.
It does not encode strings or rent buffers. Source inspection alone does not
establish the cause of a particular allocation-counter delta.

## Controlled reproduction

On Windows X64, a bounded standalone probe called the actual internal routing
method from an assembly named `Respire.Tests`. Each runtime ran once, in Release.
The probe used a no-inline measurement
method with three modes: an empty `Thread.SpinWait(100_000)` interval, 1,000 routing
validations, and one deliberately escaping `new byte[37]` allocation.

It first collected 32 samples per mode without a worker. It then collected 192
samples per mode while a separate thread retained approximately 27 MiB, replaced
1 MiB buffers, and requested nonblocking generation-2 collections. This worker
was bounded to 256 requests and 20 seconds, with bounded startup and shutdown.
Finally, after stopping the worker, it collected 32 samples per mode inside a
16 MiB no-GC region. Result arrays, anchors, serialization, and assertions were
outside the measured method.

| Runtime | Routing samples with GC worker | Empty samples with GC worker | No-GC routing samples | No-GC allocation controls |
| --- | --- | --- | --- | --- |
| .NET 8.0.31, workstation GC | One positive delta of 7,944 bytes | All zero | 32/32 zero | 32/32 exactly 64 bytes |
| .NET 10.0.12, workstation GC | One positive delta of 8,136 bytes | Positive deltas, including 7,944 bytes | 32/32 zero | 32/32 exactly 64 bytes |

Without the worker, both empty and routing controls were zero on both runtimes;
all deliberate-allocation controls were 64 bytes. The local evidence is diagnostic,
not a throughput or latency benchmark. These observations reproduce the measurement
problem but do not distinguish runtime bookkeeping from runtime allocations charged
to the measured thread, or prove the exact origin of the earlier CI deltas.

The worker follows the independent reproducer in
[dotnet/runtime#134724](https://github.com/dotnet/runtime/issues/134724), which also
records positive empty intervals on hosted Windows, Ubuntu, and macOS.
[dotnet/runtime#96836](https://github.com/dotnet/runtime/issues/96836) separately
explains why a no-inline method is needed to keep surrounding allocations outside
the JIT's measurement boundary.

## Test boundary

The regression now:

1. Runs outside parallel TUnit tests because the no-GC region is process-wide.
2. Warms both measurement paths before opening that region.
3. Measures inside a no-inline synchronous helper, with assertions after the region.
4. Requires exactly zero bytes for the original 1,000 validations.
5. Measures the same loop with a deliberate escaping 37-byte array per validation
   and requires at least 37,000 bytes, independent of object-header size.

The 16 MiB region budget is a GC reservation, not an allowed allocation threshold.
The measured control uses about 64 KiB on X64; this fixed reservation leaves headroom
for runtime and coverage activity during the short interval. TUnit 1.72.4's unkeyed
`NotInParallel` excludes all other tests, as documented in the pinned
[attribute source](https://github.com/thomhurst/TUnit/blob/db75285568cad2de096028e117e64d57201fcca6/src/TUnit.Core/Attributes/TestMetadata/NotInParallelAttribute.cs).
Failure to start the region throws. Failure to retain it is reported by
`GC.EndNoGCRegion` and wrapped with explicit boundary context when measurement itself
succeeded. An earlier measurement exception propagates unchanged. Neither case skips
the test or retries until a sample passes.
Cleanup uses `finally` and there is no `await` inside the region.

The assertion continues to run under coverage on both supported frameworks.
A separate mutation check inserts an escaping allocation directly into
`RawCommandKeyLayouts.ValidateClusterKeys`, verifies that the covered regression
fails, then restores production source. The committed positive control retains
ongoing protection against a measurement boundary that stops observing allocations.

## Ring accounting regression

Issue [#554](https://github.com/thomhurst/Respire/issues/554) records a covered net8.0
failure in `SuccessfulRingAccounting_DoesNotAllocatePerCommand`: 992 bytes instead
of zero for 1,000 successful enqueue/dequeue pairs. The
[failed job](https://github.com/thomhurst/Respire/actions/runs/36692788631/job/109813627808)
ran on diagnostic PR #553, whose changes do not modify ring production code.
The measured path updates an existing slot and volatile counters; source inspection
does not prove the origin of the observed 992 bytes.

The ring test now uses the same no-GC boundary as the raw-layout regression, extracted
into `AllocationMeasurement.WithoutConcurrentGc`. Both tests remain unkeyed
`NotInParallel` tests. Their warmed, synchronous no-inline methods keep counters and
work together; assertions and delegate construction stay outside the measured interval.
The ring requires zero bytes for all 1,000 operations, verifies every enqueue/dequeue
succeeds with the same response source, and retains the final write-offset assertion.
A second ring executes the same loop with an escaping 37-byte allocation on each
iteration and must report at least 37,000 bytes. No positive sample is discarded and
no nonzero tolerance is introduced.

The existing controlled reproduction above justifies excluding concurrent GC; it does
not establish the exact source of this new CI delta. No new local reproduction or
mutation experiment has run for #554. Fresh CI on both frameworks, including coverage, must validate
the change. Production ring behavior is unchanged.

If a nonzero sample recurs, retain the failing run and capture generation 0/1/2
`GC.CollectionCount` deltas around the counter interval, outside the counted work.
Use an allocation-stack trace to distinguish actual managed allocations from counter
behavior. Do not rerun until green or weaken the zero-byte assertion. Unkeyed
`NotInParallel` excludes other TUnit tests; unrelated runtime/background activity
can still consume the process-wide reservation and causes an explicit failure if
the no-GC region cannot be retained.

## Notification parsing regression

The first full net10.0 run after rebasing #597 onto merged #600 reported 1,728 bytes
in `RepeatedParsingAndStructEnumerationAllocateNothing` (2,259/2,260 tests passed).
The parser's current-head CI benchmarks measured zero bytes on all four layouts.
Those results do not establish the exact source of the local counter delta.

The notification test now follows the existing boundary above: unkeyed
`NotInParallel`, warmed synchronous no-inline measurements, and the shared no-GC
region. Both paths still parse, filter the prefix and enumerate 1,000 messages,
with an exact 4,000-byte subkey-length total. The ordinary path must allocate exactly
zero bytes; the positive control adds an escaping 37-byte array per iteration and
must observe at least 37,000 bytes. The test does not discard samples, retry or
relax its allocation requirement. Production parsing is unchanged.

## Remaining zero-allocation regressions

A CI sweep on 2026-10-01 found `Utf8Lookup_DoesNotAllocate` reporting 2,136 bytes
on net8.0 for a PR that did not change pub/sub routing. Five zero-allocation tests
still measured the thread counter directly: `Utf8Lookup_DoesNotAllocate`,
`RawBytesDoNotAllocate`, `UnkeyedBuiltInRouting_DoesNotAllocate`,
`UnrelatedResp3PushKindsDoNotAllocateDuringMaintenanceParsing`, and
`RawMember_AllocatesOnlyDecodedTextAndOwnedBytes`.

All five now follow the boundary above: unkeyed `NotInParallel`, warmed no-inline
measurements, the shared no-GC region, and assertions outside that region. The four
exact zero-byte tests also run a positive control with one escaping 37-byte array per
iteration. The `GeoSearchResult` test keeps its relative comparison against
decoding the same member. No assertion is relaxed.

New allocation tests must use `AllocationMeasurement.WithoutConcurrentGc` from the start.

## Dispatched flush wakes

Thread-pool growth can allocate a worker and its startup state on the signaling
thread, even inside a no-GC region. Warmup alone does not prevent later capacity
growth. The dispatched-wake allocation control therefore runs in a bounded child
process with exactly two warmed workers and a two-worker maximum. The test runner's
pool settings remain unchanged.

Both non-pool and pool producers measure 200 actual dispatched continuations using
the existing warmed no-inline methods, `WithoutConcurrentGc`, an exact zero-byte
assertion, and an escaping allocation positive control. Worker counts and collection
deltas are checked outside the measured interval. Startup, warmup, completion, and
child cleanup are bounded. A dedicated consumer would bypass the dispatch contract
being tested; increasing the allowed byte count would hide production allocations.

The parent remains an unkeyed `NotInParallel` test. The child enters through an
explicit environment mode from the normal entry point before the test runner starts,
after module initialization completes. It reuses the current apphost or .NET host
(with an explicit `DOTNET_HOST_PATH` fallback) and verifies the parent's runtime version.
The `Environment.Version` comparison is a coarse runtime version guard; it does not
compare runtime binaries or build metadata. Normal test execution delegates to
`MicrosoftTestingPlatformApplication.RunAsync`, generated by the centrally pinned
TUnit 1.72.16 package. Keep that helper contract and the entry point in sync when
upgrading TUnit; the method-specific `TUnit0034` suppression remains local to `Main`.
Exit and post-kill waits are bounded; failures separate captured stdout and stderr
with an explicit newline, even when neither stream ends with one. Failure and
timeout controls allow 30 seconds for child startup and execution on loaded CI
workers. Post-kill process waits and pipe cleanup retain separate five-second bounds.
Preserve the environment and child coverage configuration when running this control
under a coverage collector.

### Failure report

Issue [#937](https://github.com/thomhurst/Respire/issues/937) records one net8.0 CI
failure of 272 bytes, from before the child process existed. That is exactly twice the
136 bytes one worker start allocated in #1067's controlled pool-growth probe, so it fits
two worker starts in the shared pool. No allocation stack was kept, so this is a
consistent explanation, not a proven one. A later investigation could not reproduce it in
full Linux suite runs, and in-process `EventListener` sessions do not receive per-object
allocation events (`GCSampledObjectAllocationHigh` takes effect only at runtime startup).

So each measured interval now carries its own evidence:

- `Measure` writes the thread's cumulative allocated bytes and `ThreadPool.ThreadCount`
  after every wake into preallocated arrays, and records the interval's start and end
  time. The writes allocate nothing, and the counted work is unchanged.
- The child runs a `DispatchedWakeEventLog` listener on `Microsoft-Windows-DotNETRuntime`
  (GC, loader, JIT, exception and threading keywords). Native runtime events reach it on
  its own dispatcher thread, so the measured thread does not allocate for them. A
  first-call JIT event at startup calibrates the offset between event timestamps and
  `DateTime.UtcNow`.

When a sample fails, the exception message lists which wakes allocated and how much, the
worker count after each one, completed and pending pool work items, the runtime events
inside the failing interval (marked when they ran on the measuring thread), and eight
fresh samples that show whether the delta repeats. The failing sample still decides the
result. Nothing is retried, discarded or tolerated.
`DispatchedWakeFailureReportsAllocatingWakeAndRuntimeEvents` checks the report: it adds
one escaping allocation, through a method that is first JIT-compiled inside the interval,
at wake 57 of the ordinary path. The report must name that wake and the JIT event on the
measuring thread.

## Unknown-slot Cluster replica discovery

`ClusterReplicaDiscoveryTests` compares the previous per-slot
`ConcurrentDictionary<int, ClusterReplicaSet>` coalescers with the shared
coordinator. The synchronous bookkeeping comparison warms both implementations
and measures 256 persistently uncovered slots inside `WithoutConcurrentGc`, in an
unkeyed `NotInParallel` test with a no-inline measurement method. Both perform
256 probes; the comparison excludes network I/O and topology parsing. It measures
allocation, not throughput or end-to-end latency.

Windows x64 Release measurements for 256 uncovered slots:

| Runtime | Per-slot coalescers | Shared coordinator |
| --- | ---: | ---: |
| .NET 8.0.31 | 73,664 bytes | 55,288 bytes |
| .NET 10.0.12 | 73,672 bytes | 55,288 bytes |

The shared coordinator measurement includes per-slot attempt versions and the
current probe slot used to distinguish a pending attempt from a completed,
throttled attempt.

A separate concurrent comparison holds a full-coverage reply until all 256
callers have joined. The old coalescers start 256 probes; the shared coordinator
starts one. Partial-coverage tests still require a second probe for a waiting
slot that the first reply did not cover. Uncovered slots retain independent
one-second throttles, while MOVED owner changes and SMIGRATED invalidate their
old discovery attempt. Caller cancellation detaches only that caller; router
disposal cancels the physical probe.

With stable owners, N distinct, previously unattempted slots that remain
uncovered require N sequential probe rounds. This preserves each slot's own
coverage attempt when replies are partial. A completed, throttled slot returns
its cached attempt immediately even while another slot probes; a caller for the
pending probe's own slot still joins that probe.

`CoveredSlotsDoNotAllocate` checks 1,000 already-covered lookups with the same
no-GC boundary and an escaping allocation positive control. Healthy replica
selection bypasses the unknown-slot coordinator entirely.

## Cached caller attribution

`SuccessfulCachedCallersAllocateNothingWithErrorsEnabled` already used the boundary
above when it failed twice on net8.0 in the isolated telemetry step on 2026-10-11:
98,352 bytes on PR #1412 ([run 38122499608](https://github.com/thomhurst/Respire/actions/runs/38122499608))
and 49,200 bytes on docs-only PR #1413 ([run 38130458868](https://github.com/thomhurst/Respire/actions/runs/38130458868)).
Neither PR changes the cached read path, and no recent main commit does.

Per-caller attribution in [run 38133929691](https://github.com/thomhurst/Respire/actions/runs/38133929691)
put all 24,624 bytes on `GetStringAsync`, in the first measured iteration.

### Root cause: the runtime cast cache

The CI step reproduces in a Linux container (.NET SDK 10.0.401 with the 8.0.31 runtime,
4 CPUs) with the exact `--treenode-filter` from `ci.yml`: 10 of 10 runs failed.
The measured bytes are the runtime's cast cache table, not a Respire allocation.

- On .NET 8 the cast cache is a managed `int[]` (`CastHelpers.s_table`). Each entry
  is 24 bytes (six `int`s), and the table holds one extra entry for its header.
  Reflection in the reproduction read lengths of 12,294 and 24,582 `int`s, which are
  2,049 and 4,097 entries. The array sizes are 24 + 4 × length bytes: 49,200 and
  98,352 bytes. A 1,025-entry table is 24,624 bytes. These are the three CI values.
- `GC.TryStartNoGCRegion` starts with a blocking full GC. After it, the table was
  sometimes replaced by a sentinel, which shows as a different table identity before
  the region and after the measurement. In the reproduction this happened in about
  half of the runs. The replacement is not synchronous with the GC: a priming cast
  run straight after `TryStartNoGCRegion` still left the cache empty for a later cast
  in 5 of 6 flushed runs. Waiting for the finalizer pass first closed that gap, which
  is consistent with the flush running on the finalizer thread after the GC.
- After a flush, the next cast that misses the cache allocates a new table at the
  previous size. The allocating thread pays for it. On the measured paths that cast
  is in `DispatchResponseSource<string?>.Run`: `string?` code is shared canonical
  generic code, so its type checks go through the cast cache helper. The `int`
  instantiations use exact type checks, so `GetAsync<int>` never triggered it.
- Probes inside the region recorded 0 bytes for `DispatchResponseSource<string?>`
  `Start` and `CompleteInternal`, 0 bytes for `TryGetString`, and the full table size
  for a `Run` with a trivial callback. When a failing `object is object[]` cast ran
  first, it took the table allocation instead, and `Run` then allocated only its
  first-use delegate. A successful `string[]` to `object[]` cast did not refill the cache.

The table size grows with the number of distinct cast pairs the process has seen,
so the value depends on the tests that ran earlier in the same process. That fits a
failure that appeared without a change to the cached read path. This investigation
did not establish why flushes became frequent on CI after 2026-10-11 07:00Z.

### Boundary change

`AllocationMeasurement.WithoutConcurrentGc` now calls `GC.WaitForPendingFinalizers`
inside the region, after the region's GC, and then runs one failing
`object is object[]` cast before `measure`. If the GC led to a flush, the cast
reallocates the table outside the measured interval. If it did not, the cast hits
the cache and allocates nothing. In 14 reproduction runs with the change, 8 had a
flush; in all 14 the measured `Run` probe allocated only its 64-byte first-use
delegate and the measured callers allocated 0 bytes. Every caller keeps
its exact zero-byte assertion and its positive control. The fix does not skip,
retry or tolerate a sample.

The test still reports per-caller bytes, the first allocating iteration and the
GC collection counts around the region. A late iteration from a single caller points
at that caller's path. A delta on the first iteration right after the region's GC
points at runtime state that GC reset.

## Standalone batch start

`StartingOneHundredOperationsDoesNotAllocateOneTaskPerOperation` checks that starting a
100-command batch does not allocate a Task per operation. Under full-suite load in
Release it measured 10,632 bytes against its 8,000-byte limit; CI and isolated runs passed.

The 10,632 bytes are the usual 344-byte start plus the two `ArrayPool.Shared` arrays that
`RunStandaloneBatchAsync` rents for 100 operations: `ValueTask<RespValue>[128]` (7,192 bytes)
and `ErrorObservation[128]` (3,096 bytes). Draining both pools before the measured start
reproduced exactly 10,632 bytes. The arrays are rented on the starting thread and returned
on the completing thread. `ArrayPool.Shared` serves a per-thread slot first, then per-core
stacks, and gen-2 GCs (including the no-GC region's) can trim both under memory pressure.
A hit therefore depends on which threads ran earlier batches and on machine memory load.

Two more per-thread or per-process effects appeared in isolated runs:

- The first command serialized on a thread creates the thread-static 4 KiB scratch buffer
  (4,216 bytes).
- At tier 0, `MutationCommand<T>` forwards `ValidateAdmission`, `OnAccepted` and
  `GetResponseCancellationToken` to `IRespCommand` default interface methods. Each call
  boxes the struct command (136 bytes for `IncrementCommand`), so 100 operations
  allocated 40,800 bytes more. `DOTNET_TC_CallCounting=0` reproduced this on every run.
  Optimized code does not box. Tier promotion depends on other JIT activity in the
  process, so a fixed warmup cannot guarantee it.

The test now measures in a child process with `DOTNET_TieredCompilation=0`, like the
other dispatch probes. Before the counter starts, it rents and returns both arrays and
creates the scratch buffer on the measuring thread. The positive control allocates one
real `Task<long>` per operation. It must reach the 4,096-byte limit and add at least
64 bytes per operation. The old 8,000-byte limit could not catch that: 100 plain Tasks
plus the 344-byte start measured 7,544 bytes. In 20 direct child runs, the start measured
344 bytes every time. The product code is unchanged.
