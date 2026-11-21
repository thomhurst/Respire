# Pooled async methods under runtime async

On net11.0 the library compiles with runtime async (`Directory.Build.targets`). Methods
that use `PoolingAsyncValueTaskMethodBuilder` also carry `[RuntimeAsyncMethodGeneration(false)]`,
so they stay on pooled state machines. This page records whether that opt-out still pays for
itself, by measuring hot pooled methods both ways.

## Result

Keep the opt-out on every measured method. Turning a pooled method into a plain runtime-async
method added allocation on every call that suspends, in tier-1 and Tier0 code alike. Throughput
differences were within run-to-run noise.

| Method | Path measured | Pooled + opt-out (B/op) | Runtime async (B/op) | Added | Outcome |
| --- | --- | ---: | ---: | ---: | --- |
| `RespireDistributedCache.TryGetCoreAsync` | `IBufferDistributedCache.TryGetAsync` hit | 6,864 | 7,200 | +336 | keep pooled |
| `RespireClient.GetOrSetUncachedAsync` and `ProduceCacheAsideAsync` | `WithoutClientCache().GetOrSetAsync<string>` hit | 56 | 512 | +456 | keep pooled |
| `RespireJsonClient.SetUnhookedAsync` | `Json.SetAsync<T>` with a source-generated `JsonTypeInfo` | 729 | 800 | +72 | keep pooled |

Tier-1 code, sequential calls. The cache-aside pair is measured together because one awaits the
other on the same call. Baseline bytes that do not change between the columns belong to other
code on the path (for example the distributed cache's read script, and the 56-byte result string
on the cache-aside hit).

Tier0 code (methods held at Tier0 with call counting and on-stack replacement disabled) showed the
same added cost:

| Method | Pooled + opt-out (B/op) | Runtime async (B/op) | Added |
| --- | ---: | ---: | ---: |
| `TryGetCoreAsync` | 7,240 | 7,576 | +336 |
| `GetOrSetUncachedAsync` and `ProduceCacheAsideAsync` | 440 | 936 | +496 |
| `SetUnhookedAsync` | 905 | 976 | +71 |

Tier0 code boxes struct awaiters (dotnet/runtime#137275), which is why every Tier0 baseline is
higher than its tier-1 baseline.

Concurrent runs (16 callers, 2 seconds, three samples) gave the same bytes per operation as the
sequential runs, within 15 bytes. In tier-1 code their throughput ranged from about 35,000 to
80,000 operations per second, with differences between samples of one variant as large as between
variants, so they do not distinguish the two. The one clear throughput gap was the Tier0
distributed cache read: 31,600 to 35,100 operations per second pooled, 9,000 to 20,500 as runtime
async.

## Why runtime async allocates here

Runtime async reuses a continuation only while one invocation keeps suspending at the same await.
These methods are entered once per command and suspend once while the reply is in flight, so each
call that suspends allocates a fresh continuation. A pooled state machine box is returned to its
pool when the caller reads the result, so steady-state calls allocate nothing for it.

## Method

- Runtime .NET 11.0.0, SDK 11.0.100, Release build, workstation GC, default tiered compilation.
  Apple M5 Pro, macOS 27.2.1, Redis 8.2.11 in Docker on the same machine, one RESP3 connection
  with the client-side cache enabled.
- Two builds of the library from the same commit: unchanged, and with the
  `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]` and
  `[RuntimeAsyncMethodGeneration(false)]` attributes removed from the methods above. The harness
  checked through reflection that the measured methods carried `MethodImplAttributes.Async` only
  in the second build.
- The harness is a console app outside `benchmarks/`, compiled as an ordinary state machine, like
  most application code. Its loop has a single await on the public method, so the caller's own
  state machine is allocated once per loop, not per call.
- Warm-up: four rounds of 20,000 calls with a 500 ms pause between rounds, so tier-1 code is in
  place before measuring. Allocation is `GC.GetTotalAllocatedBytes(precise: true)` across 20,000
  sequential calls, three samples; process-wide, so continuations allocated on the receive thread
  are counted. Every sample of a variant was within 1 byte per operation of the others.
- Tier0 runs set `DOTNET_TC_CallCounting=0` and `DOTNET_TC_OnStackReplacement=0`.

## When to measure again

Measure again if the runtime starts reusing continuations across invocations, or if the Roslyn
handling of `AsyncMethodBuilder` under runtime async changes so that the opt-out attribute is no
longer needed or no longer available.
