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

Benchmark concurrency groups must include `github.event.label.name` alongside their existing workflow, PR and optional head/matrix scope. Every label event starts each label-triggered workflow before its jobs evaluate their conditions; an unrelated label must not cancel an active comparison whose jobs were selected. Reapplying the same label still supersedes an older run within the same group. For workflows that also support manual dispatch, the empty label segment retains their existing branch/matrix cancellation scope.

| Workflow | Label |
| --- | --- |
| `benchmark-aggregate-parsing.yml` | `run-aggregate-benchmarks` |
| `benchmark-aggregate-recovery.yml` | `run-aggregate-recovery-benchmarks` |
| `benchmark-argument-formatting.yml` | `run-argument-formatting-benchmarks` |
| `benchmark-byte-get.yml` | `run-byte-get-benchmarks` |
| `benchmark-cache-aside.yml` | `run-cache-aside-benchmarks` |
| `benchmark-cache-classification.yml` | `run-cache-classification-benchmarks` |
| `benchmark-cache-contention.yml` | `run-cache-contention-benchmarks` |
| `benchmark-cache-invalidation.yml` | `run-cache-invalidation-benchmarks` |
| `benchmark-cache-writes.yml` | `run-cache-write-benchmarks` |
| `benchmark-cache-read.yml` | `run-cache-read-benchmarks` |
| `benchmark-cache-read-parity.yml` | `run-cache-read-parity-benchmarks` |
| `benchmark-cache-publication.yml` | `run-cache-publication-benchmarks` |
| `benchmark-cache-query.yml` | `run-cache-query-benchmarks` |
| `benchmark-cache-query-hash.yml` | `run-cache-query-hash-benchmarks` |
| `benchmark-client-cache.yml` | `run-client-cache-benchmarks` |
| `benchmark-cluster-routing.yml` | `run-cluster-ready-benchmarks` |
| `benchmark-cluster-replica.yml` | `run-cluster-replica-benchmarks` |
| `benchmark-command-conversion.yml` | `run-command-conversion-benchmarks` |
| `benchmark-command-duration.yml` | `run-command-duration-benchmarks` |
| `benchmark-command-retry-owners.yml` | `run-command-retry-owner-benchmarks`, `run-command-retry-default-benchmarks`, `run-command-retry-audit-benchmarks`, `run-unit-retry-benchmarks` |
| `benchmark-command-retry.yml` | `run-command-retry-benchmarks` |
| `benchmark-command-retry-jit.yml` | `run-command-retry-jit-benchmarks` |
| `benchmark-command-retry-synchronization.yml` | `run-command-retry-synchronization-benchmarks` |
| `benchmark-command-retry-profile.yml` | `run-command-retry-profile-benchmarks` |
| `benchmark-connection-contention.yml` | `run-connection-contention-benchmarks` |
| `benchmark-connection-establishment.yml` | `run-connection-establishment-benchmarks` |
| `benchmark-dedicated-pool.yml` | `run-dedicated-pool-benchmarks` |
| `benchmark-fair-locks.yml` | `run-fair-lock-benchmarks` |
| `benchmark-fenced-locks.yml` | `run-fenced-locks-benchmarks` |
| `benchmark-hash-partial-reads.yml` | `run-hash-partial-reads-benchmarks` |
| `benchmark-hedge-ownership.yml` | `run-hedge-ownership-benchmarks` |
| `benchmark-idle-read-watchdog.yml` | `run-idle-read-watchdog-benchmarks` |
| `benchmark-inflight.yml` | `run-inflight-benchmarks` |
| `benchmark-import-health-hooks.yml` | `run-import-health-hook-benchmarks` |
| `benchmark-job-queue.yml` | `run-job-queue-benchmarks` |
| `benchmark-key-prefix.yml` | `run-key-prefix-benchmarks` |
| `benchmark-large-command-serialization.yml` | `run-large-command-serialization-benchmarks`, `run-large-command-original-benchmarks` |
| `benchmark-local-initialization.yml` | `run-local-initialization-benchmarks` |
| `benchmark-mapper.yml` | `run-mapper-benchmarks` |
| `benchmark-pinned-receive.yml` | `run-pinned-receive-benchmarks` |
| `benchmark-primitive-codec.yml` | `run-primitive-codec-benchmarks` |
| `benchmark-pubsub.yml` | `run-pubsub-benchmarks` |
| `benchmark-rate-limiters.yml` | `run-rate-limiters-benchmarks` |
| `benchmark-redlock.yml` | `run-redlock-benchmarks` |
| `benchmark-receive-compaction.yml` | `run-receive-compaction-benchmarks` |
| `benchmark-resp-framing.yml` | `run-resp-framing-benchmarks` |
| `benchmark-response-routing.yml` | `run-response-routing-benchmarks` |
| `benchmark-response-pools.yml` | `run-response-pools-benchmarks` |
| `benchmark-semaphores.yml` | `run-semaphores-benchmarks` |
| `benchmark-sentinel-routing.yml` | `run-ready-strategy-benchmarks`, `run-sentinel-benchmarks` |
| `benchmark-single-reply-completion.yml` | `run-single-reply-completion-benchmarks` |
| `benchmark-standalone-replica.yml` | `run-standalone-replica-benchmarks` |
| `benchmark-stream-add-hooks.yml` | `run-stream-add-hook-benchmarks` |
| `benchmark-thread-pool.yml` | `run-thread-pool-benchmarks` |
| `benchmark-transport.yml` | `run-transport-benchmarks` |
| `benchmark-typed-serialization.yml` | `run-typed-serialization-benchmarks` |
| `benchmark-unix-sockets.yml` | `run-unix-sockets-benchmarks` |
| `benchmark-utf8-decoder.yml` | `run-utf8-decoder-benchmarks` |
| `benchmark-utf8-suffix.yml` | `run-utf8-suffix-benchmarks` |
| `benchmark-value-codecs.yml` | `benchmark-value-codecs` |
| `benchmark-vector.yml` | `run-vector-benchmarks` |

`benchmark-comparison.yml`, `benchmark-redis-throughput.yml` and `benchmark-redis-container.yml` are not pull request comparisons. `benchmark-compare.yml` is a reusable workflow that the label-triggered workflows call; do not run it directly. Start them with `gh workflow run <file>`. `benchmark-comparison.yml` and `benchmark-redis-throughput.yml` also run weekly on a schedule; the weekly comparison run refreshes the published performance docs.

## Agent output locations

Do not write logs, reports, PR body drafts, clones or tool downloads loose in the repository's parent directory or in the main checkout. Each kind of output has one home, listed in `.claude/skills/issue-pr-loop/SKILL.md` § Isolation and ownership; evidence goes in `<repo-parent>/Respire-evidence/<lock-name>/`.

## Never wait on results

Do not wait for CI, reviews, benchmark runs, reviewer replies, or any other asynchronous result. Do not sleep, poll, run `gh pr checks --watch` or `gh run watch`, or schedule a monitor to wait for one. Instead, push or record what you have, release the item, and pick up another issue or PR. The result is usually ready when you come back to the item on a later pass. When you start a benchmark by label, move on at once and report the result in the PR description on that later pass.

## Pull request reviews

- Open every agent-created pull request as a draft (`gh pr create --draft`), whether or not the issue-pr-loop is running, and follow `.claude/skills/issue-pr-loop/SKILL.md` § Draft review lifecycle. Build and test CI does not run on drafts. Mark the PR ready only when Claude Code Review and Codex have both approved the current head and every finding is dispositioned, then immediately arm native auto-merge with `scripts/Enable-AutoMerge.ps1`. CI then runs once on that head, and GitHub merges it when the required `CI Passed` check passes. Never arm a draft, and never call `gh pr merge --auto` directly.
- Every review cycle, check both review threads (`pulls/<N>/reviews`, `pulls/<N>/comments`, GraphQL `reviewThreads`) and PR conversation comments (`issues/<N>/comments`). Reviewers differ: Claude often posts its review as a conversation comment, while other agents use review threads. A finding on either surface needs a disposition before merge.
- Resolve each PR review thread, whether a human or a bot opened it, as soon as you have dispositioned it: the fix is pushed to the PR head and your reply names the commit, or your reply pushes back on the finding with evidence. Leave a thread open only while it has no disposition. If the reviewer replies after your disposition, unresolve the thread and handle the reply.

## Autonomous self-improvement

Treat every problem you notice as work to act on, whether you find it while testing, developing, reading code, reviewing, or doing anything else. Examples include bugs, flaky or failing tests, incorrect docs, misleading comments, broken tooling, and gaps in these instructions.

- If the fix is trivial and low-risk, fix it as part of the current work and mention it in the PR description.
- If the fix is out of scope for the current work, raise a GitHub issue at the point you discover it so it can be fixed later. Include what you saw, where you saw it (file and line, command, or CI run), how to reproduce it, and the expected behavior. Search existing issues first, and add to a matching issue instead of opening a duplicate.
- Do not ignore a problem because it existed before your change or is unrelated to it.
