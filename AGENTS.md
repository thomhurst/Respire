# Repository Guidelines

## Release notes

Do not create or maintain a release notes or changelog file (for example `docs/RELEASE_NOTES.md` or `CHANGELOG.md`). GitHub generates release notes from merged PRs, and a shared file causes merge conflicts between PRs. When a rebase or merge hits a conflict on `docs/RELEASE_NOTES.md`, resolve it by deleting the file (`git rm docs/RELEASE_NOTES.md`). Never restore it. Describe user-facing changes in the PR title and description instead.

## Design docs

Do not recreate `docs/API_DESIGN.md` or add another spec, status list, or roadmap file that feature PRs must update. Such shared files caused merge conflicts between most PRs. Document user-facing features in `website/docs` (the guide for the feature, plus `website/docs/roadmap.md` when status changes). When a rebase or merge hits a conflict on `docs/API_DESIGN.md`, resolve it by deleting the file (`git rm docs/API_DESIGN.md`). Never restore it.

## Source escape sequences

Several PR builds failed with `CS1010: Newline in constant` because a tool expanded `\r\n` inside a C# string literal into real line breaks before the file was written. Write source with file-editing tools, or with a quoted heredoc (`<<'EOF'`). Do not use `echo -e`, `printf` format strings, interpolated PowerShell strings, or non-raw string literals in a generating script for this. Build the changed projects before you push.

## Allocation tests

Zero-allocation assertions must use `AllocationMeasurement.WithoutConcurrentGc` with an unkeyed `[NotInParallel]` test, warmed no-inline measurement methods, and a positive control. A bare `GC.GetAllocatedBytesForCurrentThread()` delta is flaky on CI. See `docs/ALLOCATION_MEASUREMENT.md`.

## Benchmark workflows

Benchmark workflows (`.github/workflows/benchmark-*.yml`) run on net10.0 only, to save runner minutes. Do not add a net8.0 matrix leg or an `8.0.x` SDK install to a benchmark workflow, and do not write net8.0-only benchmark gates. net8.0 correctness coverage belongs in `ci.yml`, `test-full-net8.yml` and `stress-tests.yml`.

## Running benchmarks

Pull request benchmark workflows start only when someone adds their label. They have no path triggers, so ordinary PR pushes do not run them. Run a benchmark only when you change a hot path and want to check its performance. To start one, add the workflow's label to the pull request, for example `gh pr edit <number> --add-label run-transport-benchmarks`. The run measures the pull request as it is when you add the label. To measure a later push, remove the label and add it again. Choose the benchmark that covers the code you changed, and report the result in the PR description. Do not add `paths`, `push`, `opened` or `synchronize` triggers to a benchmark workflow. A new benchmark workflow gets its own `run-<name>-benchmarks` label and a `pull_request: types: [labeled]` trigger. Create the label with `gh label create` if it does not exist.

| Workflow | Label |
| --- | --- |
| `benchmark-aggregate-parsing.yml` | `run-aggregate-benchmarks` |
| `benchmark-byte-get.yml` | `run-byte-get-benchmarks` |
| `benchmark-cache-aside.yml` | `run-cache-aside-benchmarks` |
| `benchmark-cache-contention.yml` | `run-cache-contention-benchmarks` |
| `benchmark-cache-invalidation.yml` | `run-cache-invalidation-benchmarks` |
| `benchmark-cache-read.yml` | `run-cache-read-benchmarks` |
| `benchmark-client-cache.yml` | `run-client-cache-benchmarks` |
| `benchmark-cluster-routing.yml` | `run-cluster-ready-benchmarks` |
| `benchmark-command-conversion.yml` | `run-command-conversion-benchmarks` |
| `benchmark-connection-contention.yml` | `run-connection-contention-benchmarks` |
| `benchmark-dedicated-pool.yml` | `run-dedicated-pool-benchmarks` |
| `benchmark-fenced-locks.yml` | `run-fenced-locks-benchmarks` |
| `benchmark-hash-partial-reads.yml` | `run-hash-partial-reads-benchmarks` |
| `benchmark-idle-read-watchdog.yml` | `run-idle-read-watchdog-benchmarks` |
| `benchmark-inflight.yml` | `run-inflight-benchmarks` |
| `benchmark-key-prefix.yml` | `run-key-prefix-benchmarks` |
| `benchmark-pinned-receive.yml` | `run-pinned-receive-benchmarks` |
| `benchmark-primitive-codec.yml` | `run-primitive-codec-benchmarks` |
| `benchmark-pubsub.yml` | `run-pubsub-benchmarks` |
| `benchmark-rate-limiters.yml` | `run-rate-limiters-benchmarks` |
| `benchmark-receive-compaction.yml` | `run-receive-compaction-benchmarks` |
| `benchmark-resp-framing.yml` | `run-resp-framing-benchmarks` |
| `benchmark-response-routing.yml` | `run-response-routing-benchmarks` |
| `benchmark-response-pools.yml` | `run-response-pools-benchmarks` |
| `benchmark-semaphores.yml` | `run-semaphores-benchmarks` |
| `benchmark-sentinel-routing.yml` | `run-ready-strategy-benchmarks`, `run-sentinel-benchmarks` |
| `benchmark-thread-pool.yml` | `run-thread-pool-benchmarks` |
| `benchmark-transport.yml` | `run-transport-benchmarks` |
| `benchmark-typed-serialization.yml` | `run-typed-serialization-benchmarks` |
| `benchmark-unix-sockets.yml` | `run-unix-sockets-benchmarks` |
| `benchmark-utf8-decoder.yml` | `run-utf8-decoder-benchmarks` |
| `benchmark-utf8-suffix.yml` | `run-utf8-suffix-benchmarks` |
| `benchmark-value-codecs.yml` | `benchmark-value-codecs` |
| `benchmark-vector.yml` | `run-vector-benchmarks` |

`benchmark-comparison.yml`, `benchmark-redis-throughput.yml` and `benchmark-redis-container.yml` are not pull request comparisons. Start them with `gh workflow run <file>`. `benchmark-comparison.yml` and `benchmark-redis-throughput.yml` also run weekly on a schedule; the weekly comparison run refreshes the published performance docs.

## Pull request reviews

- Resolve each PR review thread, whether a human or a bot opened it, as soon as you have dispositioned it: the fix is pushed to the PR head and your reply names the commit, or your reply pushes back on the finding with evidence. Leave a thread open only while it has no disposition. If the reviewer replies after your disposition, unresolve the thread and handle the reply.
