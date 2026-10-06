# TimeSeries follower leases: investigation #944

> Frozen measurements against `b05bd7db8719bab5b1b319bf91c200dada4e2a19`.
> These are local investigation results, not a maintained performance baseline.

Retain per-page leases. The persistent prototype does not improve the measured
fast-consumer workloads, holds a connection for every paused follower, and prevents
the retired pool from draining until those followers resume or dispose. No shared
transport primitive or module-side routing bypass is introduced.

## What was compared

The real `RespireTimeSeriesClient.FollowAsync` consumes 64 existing samples with
page sizes 1, 8, and 64. Each invocation checks the sample-value sum, disposes the
enumerator, and either consumes immediately or requests `Task.Delay(1)` after
every sample. Setup populates the series and warms the dedicated connection.
One reported operation is the complete 64-sample workload, not one page.

Both arms use the same compiled transport. The per-page arm runs the normal
rent/return path with an inactive prototype hook. The persistent arm installs an
`AsyncLocal` scope that retains the returned connection and reuses it on the next
rental from the same current pool. It checks connection health, pool retirement,
maintenance publication identity, and preferred zone. A changed pool returns the
old lease; discard forgets it; scope disposal returns it. `SendBlockingAsync`,
route selection, reply ownership, and error handling still execute for every page.
The TimeSeries module is unchanged.

This is deliberately a rejected prototype, not a production lease implementation.
It keeps its lease while the application pauses. Cluster handoff and replica
fallback were not independently exercised with the prototype. Shipping no
transport change preserves those existing contracts instead of claiming that the
prototype is ready for them. The inactive hook adds a small cost to the baseline,
so absolute numbers are not an unmodified-release guarantee.

## Environment and timing

- Windows 11 x64, Intel Core i7-12700K, .NET SDK 10.0.401, .NET 10.0.12,
  concurrent workstation GC, `DOTNET_PROCESSOR_COUNT=2`.
- BenchmarkDotNet 0.15.8, `InProcessEmitToolchain`, default statistical settings.
  Follower runs use `UnrollFactor=1`; slow-consumer runs use one invocation per
  iteration because one invocation already takes about one second.
- Docker/WSL2 Redis 8.10.2, pinned image
  `redis@sha256:3811787313eba226a2ef38658c6ccb91cd5e110edc89c37767de373120a0e5a0`.
  A dedicated server uses a random loopback port; persistence is disabled.
- Shared workstation, single follower per timing case, existing history rather
  than a live producer. These measurements do not characterize contended pool
  locks, production tail latency, Linux clients, or .NET 8.

Fast-consumer results, per 64 samples:

| Page size | Per-page mean | Persistent mean | Persistent/baseline |
| --- | ---: | ---: | ---: |
| 1 | 19.5051 ms | 19.9776 ms | 1.02 |
| 8 | 2.6040 ms | 2.6557 ms | 1.02 |
| 64 | 357.1 us | 383.3 us | 1.07 |

The [full fast report](investigations/timeseries-follower-leases/followers-fast.md)
includes confidence intervals, variability, and allocations. The smaller
differences should not be treated as precise production regressions; none of
these cases demonstrates a benefit from retention.

Slow-consumer results, per 64 samples:

| Page size | Per-page mean | Persistent mean | Persistent/baseline |
| --- | ---: | ---: | ---: |
| 1 | 984.6 ms | 986.9 ms | 1.00 |
| 8 | 993.2 ms | 989.7 ms | 1.00 |
| 64 | 996.2 ms | 997.4 ms | 1.00 |

All three pairs have overlapping reported timing intervals. Windows timer
resolution makes the requested 1 ms delay much longer: these are approximately
one-second consumption workloads, not 64 ms workloads. See the
[full slow report](investigations/timeseries-follower-leases/followers-slow.md).

A separate uncontended pool microbenchmark measures rent/return at **38.40 ns**
(reported error 0.292 ns), versus **11.72 ns** (0.139 ns) for a retained connection's
health/retirement check. Neither reports managed allocations. The latter is a
lower-work comparison, not the complete prototype or a replacement for routing.
At the measured rent/return mean, 64 rentals account for about 2.46 us, compared
with the 19.5 ms page-size-one workload. This arithmetic is only a local order of
magnitude; it does not bound contention or production latency. See the
[pool report](investigations/timeseries-follower-leases/pool.md).

## Paused consumers, competing readers, and cleanup

Eight followers start sequentially on one client and pause after their first
sample. An independent blocking-capable `TS.READ` then reads an existing sample.
Server `CLIENT LIST` counts include the client's one multiplexed connection.

| Observation | Per-page | Persistent prototype |
| --- | ---: | ---: |
| Borrowed leases while eight consumers pause | 0 | 8 |
| Server connections before independent reader | 2 | 9 |
| Server connections after independent reader | 2 | 10 |
| Independent reader returns one sample | Yes | Yes |
| Pool retirement completes while consumers stay paused | Yes | No |
| Borrowed leases after enumerator/scope disposal | 0 | 0 |
| Borrowed leases after canceling a blocked read | 0 | 0 |

The pool limits **idle** connections to four; it does not impose an active-rental
limit. The competing reader is therefore not starved: persistence makes it open
another physical connection. The experiment does not establish queue fairness
under a server connection limit. Its single latency observations (0.502 ms versus
1.408 ms) are diagnostics, not a latency benchmark. Retirement was observed for
100 ms while consumers stayed paused, then both modes drained after release.
The [captured output](investigations/timeseries-follower-leases/fairness.txt)
records these observations and cancellation cleanup.

## Reproduction and evidence

The [archive](investigations/timeseries-follower-leases/) contains the exact
task-scoped harness, prototype source, production patch, reports, and logs.
`fixtures.sha256.json` records file hashes; `.gitattributes` preserves their bytes.
These `.txt` files are evidence, not part of the permanent benchmark suite.

Use a disposable worktree at the recorded base. Copy the three source/project
fixtures to these destinations, removing only the final `.txt` suffix:

| Archived file | Destination in disposable worktree |
| --- | --- |
| `Program.cs.txt` | `artifacts/issue-944-investigation/Program.cs` |
| `Issue944.Benchmarks.csproj.txt` | `artifacts/issue-944-investigation/Issue944.Benchmarks.csproj` |
| `Issue944LeaseScope.cs.txt` | `src/Respire/Internal/Issue944LeaseScope.cs` |

Apply `prototype.patch` with `git apply --check` followed by `git apply`. The patch
adds the temporary pool hooks and the harness's friend-assembly declaration.
Start an owned container from the pinned image with `--save '' --appendonly no`
and a free loopback port. Set `RESPIRE_944_PORT` to that port. Build from the
worktree root:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'build', 'artifacts/issue-944-investigation/Issue944.Benchmarks.csproj',
    '-c', 'Release', '-f', 'net10.0')
```

From the harness directory, use the absolute repository guard path and execute
`bin/Release/net10.0/Issue944.Benchmarks.dll` with these argument sets. Set
`RESPIRE_944_OUTPUT` to a different absolute log path for each run.

| Run | Environment | Arguments after DLL |
| --- | --- | --- |
| Correctness dry run | Delay/page variables unset | `--filter * --job Dry --inProcess --noOverwrite --artifacts dry` |
| Isolated bookkeeping | Delay/page variables unset | `--filter *PoolBookkeeping* --inProcess --noOverwrite --artifacts pool` |
| Fast consumers | `RESPIRE_944_DELAY=0` | `--filter *FollowerLeases* --inProcess --unrollFactor 1 --noOverwrite --artifacts fast` |
| Slow consumers | `RESPIRE_944_DELAY=1` | `--filter *FollowerLeases* --inProcess --unrollFactor 1 --invocationCount 1 --noOverwrite --artifacts slow` |
| Resource/cleanup probe | Delay/page variables unset | `--fairness` |

`RESPIRE_944_PAGE` can select one page size; otherwise all three run. Stop and
remove only the container created for this reproduction. Discard the disposable
prototype checkout rather than applying its transport edits to a working branch.

All local .NET commands retain the repository's 600-second/2048-MB guard limits.
Initial runs with a duplicate project name executed no benchmarks. The normal
out-of-process toolchain then hit BenchmarkDotNet's 120-second generated-build
timeout; the already compiled Release harness ran through its supported
in-process toolchain. A combined follower run was stopped and split by consumer
speed to bound its duration. Those attempts and all Dry timings are excluded
from the measured decision.
