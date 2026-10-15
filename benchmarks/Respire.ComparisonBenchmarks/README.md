# Client comparison benchmarks

The `benchmark-comparison.yml` workflow runs on Linux with .NET 10 and a Redis
8.10 Docker service exposed on localhost. The weekly run uses the repository's
StackExchange.Redis version. Manual runs can select pinned `3.4.5` or
`4.0.127-alpha` baselines:

```sh
gh workflow run benchmark-comparison.yml -f stackexchange-version=3.4.5 -f job=medium -f filter='*'
gh workflow run benchmark-comparison.yml -f stackexchange-version=4.0.127-alpha -f job=medium -f filter='*'
```

Use `--ref <branch>` to measure benchmark changes before merging. Start runs and
return to their artifacts later; the issue/PR loop must not wait for them.
Use `job=dry` with a focused filter to validate setup without producing meaningful
measurements. Custom-version and dry runs never overwrite the weekly docs page.

Each artifact includes BenchmarkDotNet reports, `project.assets.json` with resolved
dependency versions, and `rig.txt` with the commit, run URL, runner image, CPU, OS,
.NET SDK/runtime and Redis image identity. Custom-version artifact names include
the selected version. Compare Respire with StackExchange.Redis measured in the
same job; separate hosted runners need not have identical hardware.

| Suite | Measurement |
| --- | --- |
| `CommonOperationsBenchmarks` | Single operations, 1/10 KB writes, 50 concurrent GETs and 200 pipelined GETs; StackExchange uses the established `IDatabase` Task API in both versions. |
| `PubSubComparisonBenchmarks` | One publish through consumption of its message by one subscriber, including UTF-8 string decoding; both clients use RESP3 with separate command/subscription connections. |
| `ClientSideCachingBenchmarks` | Respire local hits and both clients' uncached server reads. StackExchange caching is deliberately disabled here, including on 4.0. |
| `ClientCacheComparisonBenchmarks` | 4.0 only: both clients' local GET hits, for existing and missing keys. StackExchange uses `Strings.GetAsync` (ValueTask); both return strings. |

The 4.0 cache comparison uses RESP3 broadcast tracking, isolated prefixes, 64 MiB /
10,000-entry bounds and one-day local lifetimes so expiry does not interrupt steady
hits. Setup warms the default admission policy and checks Redis GET counters with
an uncached positive control, then verifies that repeated reads on both clients
produce the expected value without reaching Redis. Use an isolated Redis instance:
other GET traffic makes that check fail. These cases measure hot hits, not cache
admission, invalidation or expiration cost.

For local validation, set `STACKEXCHANGE_REDIS_BENCHMARK_VERSION` before **both**
building and running. It is inherited by BenchmarkDotNet's generated builds. The
override lives in this benchmark directory and does not upgrade library packages.
Omit the variable (or set `repository`) to use the normal central dependency.

```sh
export STACKEXCHANGE_REDIS_BENCHMARK_VERSION=4.0.127-alpha
dotnet build benchmarks/Respire.ComparisonBenchmarks -c Release -f net10.0
dotnet run --project benchmarks/Respire.ComparisonBenchmarks -c Release -f net10.0 --no-build -- \
  --filter '*ClientCacheComparisonBenchmarks.*' '*PubSubComparisonBenchmarks.*' --job Dry
```

`REDIS_HOST` and `REDIS_PORT` select an existing isolated server; without them,
each benchmark case owns a throwaway Testcontainer. Agents must invoke local .NET
commands through `scripts/Invoke-AgentDotNet.ps1` and leave full measurement runs
to CI. Dry runs are correctness checks, not publishable performance results.

### Pub/sub latency investigation

Add `-f profile-pubsub=true` to a focused manual comparison to collect diagnostics
alongside the unchanged publish-to-consume measurements:

```sh
gh workflow run benchmark-comparison.yml --ref <branch> -f stackexchange-version=3.4.5 -f job=medium -f filter='*PubSubComparisonBenchmarks*' -f profile-pubsub=true
gh workflow run benchmark-comparison.yml --ref <branch> -f stackexchange-version=4.0.127-alpha -f job=medium -f filter='*PubSubComparisonBenchmarks*' -f profile-pubsub=true
```

The artifact's `pubsub-profile/phases.json` retains 5,000 samples per client after
500 warm-up operations each, alternating client order. Each sample records elapsed
time to the PUBLISH acknowledgement, the remaining time through message consumption
and UTF-8 decoding, and whether the read completed synchronously. Both phases keep
the real consumer and Respire's gap guard; the probe also verifies recipient count
and payload. A two-minute cancellation deadline bounds the sampling loop.

Acknowledgement time includes client continuations and the server round trip.
Delivery can overlap it, so the after-ack phase is **residual wait/consumption**, not
the total delivery cost. Timestamping and cancellation instrumentation perturb these
samples. They diagnose where to investigate, and are not a client speed comparison.

`pubsub-profile/eventpipe/` contains a separate Short BDN run with CPU samples and
threading diagnostics. Its timing includes profiler overhead; use only the ordinary
Medium reports at the artifact root for latency, distributions and allocation claims.
The nested profiler reports are excluded from automatic performance documentation.
Inspect both clients' receive, publication and consumer stacks before proposing a
scheduling change. CPU sampling alone cannot attribute off-CPU scheduler delays.

The probe is also available locally with `--profile-pubsub <output.json>` instead
of the BDN arguments. It uses the same Redis endpoint and dependency version settings.
