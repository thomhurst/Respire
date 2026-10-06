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
Exit and post-kill waits are bounded; failures include captured stdout and stderr.
Preserve the environment and child coverage configuration when running this control
under a coverage collector.

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
