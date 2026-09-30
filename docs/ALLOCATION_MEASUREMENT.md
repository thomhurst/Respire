# Raw key-layout allocation regression

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
method from an assembly named `Respire.Tests`. Each runtime ran once, in Release,
under the shared performance reservation. The probe used a no-inline measurement
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
Failure to start the region throws. Failure to retain it is reported by
`GC.EndNoGCRegion`; neither case skips the test or retries until a sample passes.
Cleanup uses `finally` and there is no `await` inside the region.

The assertion continues to run under coverage on both supported frameworks.
A separate mutation check inserts an escaping allocation directly into
`RawCommandKeyLayouts.ValidateClusterKeys`, verifies that the covered regression
fails, then restores production source. The committed positive control retains
ongoing protection against a measurement boundary that stops observing allocations.
