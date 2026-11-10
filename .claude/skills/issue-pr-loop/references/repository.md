# Respire

Read [AGENTS.md](../../../../AGENTS.md) first. The mechanics this loop defers to:

| Need | Owner |
|---|---|
| One PR's review cycle: fetch every surface, verify findings, fix, one push, reply, resolve and unresolve threads | [Review cycle](#review-cycle) below and `AGENTS.md` § Pull request reviews |
| Merge protocol | [Merge protocol](#merge-protocol) below |
| Out-of-scope filing bar and procedure | `AGENTS.md` § Autonomous self-improvement and § Writing issues |
| Native sub-issue and `blocked_by` writes | [Issue relationships](#issue-relationships) below |
| Worktree location, lock registration, evidence directory, cleanup | [Worktrees](#worktrees) and [Output locations](#output-locations) below, and [WorktreeLifecycle.md](../../../../scripts/WorktreeLifecycle.md) |
| PR review lifecycle | [draft-review.md](draft-review.md), with [Draft review lifecycle](#draft-review-lifecycle) below |
| Pending CI, reviews, benchmarks | `AGENTS.md` § Never wait on results |
| Validation and shipping | [Validation](#validation) and [Shipping](#shipping) below |

Lock owner variable: `RESPIRE_AGENT_LOCK_OWNER_ID` (read by `scripts/AgentLocks.ps1` after `-OwnerId`, before `CODEX_THREAD_ID`).

## Owner-reserved decisions

No issue class is reserved for the owner beyond the `[human]` category in `SKILL.md` § Resolve, do not defer. A breaking change is allowed on a milestone that permits breaking changes.

## Issue labels

The repository has no `priority:*` or `ci` labels, so every issue sits in one tier of `pick-an-issue.md`; apply its within-tier order, judging pipeline-health issues by content.

## Validation

- Every local .NET command uses `scripts/Invoke-AgentDotNet.ps1`, invoked in-process in PowerShell (`& scripts/Invoke-AgentDotNet.ps1 -DotNetArguments @('build')`). Allow the outer timeout at least 30 seconds beyond the guard; exit 124/137 is a validation limit, not permission to raise limits.
- On Windows, retain logs by redirecting an outer `pwsh -NoProfile -File <runner.ps1>` invocation whose runner calls the guard in-process. Redirecting only `& scripts/Invoke-AgentDotNet.ps1 ... *> file.log` can leave an empty file because the guarded native process inherits console handles.
- Run relevant TUnit executable projects under `tests/`, selecting a supported framework and `--treenode-filter` for focus. Set `$env:TUNIT_DISABLE_HTML_REPORTER = 'true'` for local runs, matching CI: the reporter's process-wide ActivityListener changes telemetry-path selection and breaks tests that require no external listener. Prefer `Respire.Tests`/`Respire.Pipeline.Tests` for fast feedback; integration tests require Docker/Testcontainers Redis. Website changes require `npm run build --prefix website`.
- When the full `Respire.Tests` compilation exceeds the guard, use `-p:RespireTestFilter=<source-globs>` for both the guarded build and `run --no-build`; see `CONTRIBUTING.md`. This filters compilation before TUnit generation, keeps the real runner/probes, and isolates focused outputs. Keep the 2048 MB limit and diagnostics unchanged; full-suite validation remains in CI.
- Library awaits use `ConfigureAwait(false)`. Never hand-edit `src/Respire/RespireCommands.g.cs`; change `tools/Generate-CommandCatalog.ps1` or an appropriate manual partial/extension. Review/simplification must respect that boundary.
- Preserve networking FIFO response order, buffer reference counts, uninterrupted in-flight writes, and the persistent flush task.
- Never run the full benchmark suite as an agent; benchmark CI owns performance validation. Benchmark workflows run on net11.0 only; do not add a net10.0 benchmark leg or `10.0.x` SDK install to them. Report unavailable Docker validation.
- Aspire samples and the Aspire template smoke test own their AppHost resources. Other integration tests use Testcontainers for ephemeral Redis. Stop only resources started by the current validation; preserve shared lock Redis and peers' resources.

## Shipping

- Follow local validation, resource limits, target frameworks, generated-code boundaries, and performance requirements. Use TDD for bugs and behavior changes where feasible; explain exceptions. Run focused and relevant broader checks, including docs and frontend checks when affected.
- Review the full diff for correctness, reuse, and efficiency (`/simplify` when available, otherwise equivalent review). Preserve performance and contracts, and batch related fixes into one push.
- Use commentary while active.

## Worktrees

Add to the shared-checkout capture in `SKILL.md` § Isolation and ownership:

```powershell
$repoName = ($repoSlug -split '/')[-1]
$worktreeRoot = Join-Path (Split-Path $repo -Parent) ($repoName + '-worktrees')
```

- Verify the repository and intended base; examples use `origin/main`. Use a writable temporary `<repoName>-worktrees` root if needed.
- No sparse-checkout or custom push refspec applies here.
- PR setup takes no temporary Git lock here. The sweep unlocks a leftover `PR checkout setup` or `Issue checkout setup` lock only once it is older than the two-hour Redis TTL.
- Keep explicit lock names when registering a path.
- Use repository scripts for merged-worktree cleanup; `[gone]` or ancestry alone is insufficient. The sweep removes merged worktrees, and their merged local branches, after reserving every lock that can own them; leftover tracked and untracked changes are first saved to a `retained-worktrees/<item>-wip-<sha12>` branch. It also removes the checkout of a closed `pr-<N>`/`issue-<N>` item that no merge signal matches, keeping its branch and saving uncommitted changes and detached commits to `retained-worktrees/` branches. It preserves locked, owned, open-PR, harness-managed and unidentified worktrees, locally divergent worktrees of open items, and any checkout with unknown ignored files.
- Inspect untracked source before cleanup: scripts may clear build artifacts and remove merged checkouts. Manually remove abandoned worktrees only after verifying repository/path, ownership, and absence of valuable unpublished work.
- Stop only services, processes, and containers started for this worktree. Never blanket-delete containers or volumes, or stop shared lock Redis. Follow local lifecycle guidance; use Aspire only where an AppHost exists.

## Output locations

Write nothing loose beside the repository. Every file an agent produces outside its worktree has one home, so cleanup can find it:

- Evidence (build and test logs, reports, PR and comment body drafts, diffs, validation projects) goes in `<repo-parent>/<repoName>-evidence/<lock-name>/`, for example `C:/git/<repoName>-evidence/issue-<N>/build.log`. `scripts/Remove-MergedWorktrees.ps1` deletes a `pr-<N>` or `issue-<N>` entry once that item is closed (merged or not) and no registered worktree uses it, and any other entry once its newest file is older than `-EvidenceRetentionDays` (default 7). Release keeps the evidence of an open item; anything that must outlive the item belongs in a durable uploaded artifact.
- Long command output (builds, tests, `gh api` dumps, logs) goes to a file under the evidence root, with or without subagents.
- Throwaway checkouts and baselines are worktrees under `$worktreeRoot`, never plain copies or clones.
- Downloaded SDKs and CLI tools go in `<repo-parent>/toolchains/<name>-<version>/`, shared by every repository.
- Never write to `<repo-parent>` itself (for example `C:/git`) or to the shared checkout.

## Review cycle

Run under the PR lock.

- Inspect all review surfaces, including pagination and replies: REST `pulls/<N>/reviews`, `pulls/<N>/comments`, `issues/<N>/comments` under `repos/{owner}/{repo}`, and GraphQL `reviewThreads` with IDs, resolution state, bodies, authors, and timestamps.
- Never infer a reviewer's surface from its identity. Treat a conversation comment that carries findings or a verdict as a review; it has no thread to resolve, so disposition it with one PR comment that quotes or links it.
- A `COMMENTED` review body can block with zero unresolved threads. Address outstanding concerns, `CHANGES_REQUESTED`, current inline findings, and unresolved threads. Empty acknowledgments are non-blocking. Assess older findings against the current head and later discussion; uncertain concerns block merging.
- Verify every finding against code and classify it. **Blocking**: a correctness, concurrency, security, contract, or performance defect, or missing tests for changed behavior. Fix each valid blocking finding in this PR. **Non-blocking**: naming, style, wording, optional refactors, extra tests for unchanged behavior, and "consider" suggestions. Implement a non-blocking suggestion only when it is small and you already push a blocking fix. Otherwise decline it in the disposition with a one-line reason and push nothing for it.
- Decline a wrong finding with evidence from the code. Move architectural work that is genuinely out of scope to a filed follow-up issue, linked in the reply. "Not done" or "left as is" without a classification, reason, or linked issue is not a disposition.
- Reply once per review covering each finding, and reply on each thread. Discuss disagreement once; implement reaffirmed blocking feedback. A disposition-only reply on the current head needs no new push, so it does not start another review cycle.
- Resolve every review thread, whether a human or a bot opened it, as soon as it is dispositioned: the fix commit is in the remote PR head and the reply names it, or the reply pushes back on the finding with evidence. Do not wait for another review/CI cycle to resolve a dispositioned thread; that cycle still gates merging. Leave a thread open only while it has no disposition: the fix is not pushed, the item waits on the user, or the reviewer replied after your disposition.
- On each later review cycle, re-fetch every thread, resolved ones included; a reviewer reply newer than your disposition reopens it: run GraphQL `unresolveReviewThread` and handle it as reaffirmed feedback.
- Reply to addressed review-body findings and request re-review when an outstanding changes-requested review needs updating; do not dismiss reviews to bypass approval requirements.
- Post each thread reply with `pwsh scripts/Reply-ReviewThread.ps1 -Pr <N> -CommentId <first comment id> -BodyFile <file> -Resolve`: write the body to a file first, never through an interpolated PowerShell string, which can post `System.Collections.Hashtable.Body` instead of the disposition. The script rejects such text, confirms GitHub stored the exact body, and then runs GraphQL `resolveReviewThread`. Then re-fetch reviews, threads, checks, and head SHA.
- Claude Code Review posts each review as a PR issue comment from `github-actions[bot]` (marked `<!-- claude-code-review -->`), not as a GitHub review, so it appears only in `issues/<N>/comments`. Treat each one as a full review: its numbered or bold findings and design suggestions are review findings even though there is no thread to resolve. `Assert-PrGreen.ps1` requires a review whose `<!-- REVIEW_HEAD_SHA: <full commit SHA> -->` marker matches the current head. Its latest matching review must carry `REVIEW_VERDICT: CLEAR` or have a later maintainer disposition (write access or higher). That reply must dispose of every finding and include both `<!-- REVIEW_DISPOSITION -->` and the matching `REVIEW_HEAD_SHA` marker. A stale or unstamped review or disposition cannot clear a new head. Only for a PR authored by the Dependabot or Renovate bot, when every Claude review check is explicitly SKIPPED, stale reviews do not require a replacement; any current-head manual review still needs clearance or disposition. Other later comments do not count.

## Draft review lifecycle

[draft-review.md](draft-review.md) is the PR review lifecycle. `AGENTS.md` § Pull request reviews applies it to every agent-created PR, whether or not the loop is running.

- The required reviewers are Claude Code Review and Codex (`chatgpt-codex-connector[bot]`). CodeRabbit and Greptile also comment; dispose of their findings like any other, but their verdicts are not needed to mark a PR ready.
- Claude Code Review reviews drafts on open and on every push; marking a PR ready does not start another review, so the draft review of the current head is the one `Assert-PrGreen.ps1` reads, and the completed `claude-review` check lets `Enable-AutoMerge.ps1` arm right after promotion. Its approval is a review for the current `REVIEW_HEAD_SHA` that carries `REVIEW_VERDICT: CLEAR`, or a `REVIEW_DISPOSITION` reply as described in [Review cycle](#review-cycle).
- Codex reviews automatically when a PR opens ready, is marked ready, or gets new commits while ready, but never reviews a draft by itself. Findings from the review it runs on promotion are late reviews under [Fixing a ready PR](#fixing-a-ready-pr). While a PR is a draft, request it with `gh pr comment <N> --body-file <file>`, where the body starts with `<!-- issue-pr-loop -->` and a separate `<!-- codex-review:<full head SHA> -->` marker, followed by `@codex review`. A request is pending for 15 minutes after its `createdAt`. When Codex has posted no review, summary update, or thumbs-up for this head by then, the request was dropped; post a fresh one with the same markers. An eyes reaction alone means nothing: Codex often adds one and never reviews. Never tag Codex on a ready PR. Codex's approval is either its `<!-- codex-pull-request-review-summary -->` issue comment whose `codex-review-state:v1` payload has `code_review.commit_sha` equal to the full current head, `status: completed`, `has_any_findings: false` and `has_unresolved_findings: false`, or a thumbs-up from `chatgpt-codex-connector[bot]` on this head's request (check the author through the reactions API). Codex files findings as inline review threads with a priority badge. The summary comment is edited in place, so re-read it on each visit; an older head's summary is not approval.
- Draft CI: `ci.yml`, `modular-pipeline.yml`, `docs.yml`, `agent-guard.yml` and the path-filtered regression workflows skip every PR job on drafts before a runner starts, and run when the PR is marked ready. `converted_to_draft` cancels their in-flight runs through workflow concurrency. `test-full-net10.yml` also skips drafts and runs on readiness when its label is present. Label-triggered benchmark and server-compatibility workflows still run on drafts, because adding their label is an explicit request.
- Auto-merge: the `Main` ruleset requires one status check, `CI Passed`, from the `ci-passed` job in `ci.yml`. It needs every other `ci.yml` job, and on pull requests it waits for the newest non-skipped run of every other pull request workflow on the head (`scripts/workflows/PrWorkflowGate.py`; label-triggered benchmark and server-compatibility runs excluded) and fails if any failed. Its name is an expression, so the run skipped on a draft reports the expression text and cannot satisfy the requirement; a fixed-name skipped job would report as a passing `CI Passed`. `scripts/workflows/CiPassedPolicy.py` enforces the needs list, the name, and the draft guards. The `disarm-auto-merge` job in `claude-code-review.yml` turns auto-merge off on every push to a PR that has it armed.

Automated review rounds (rework budget): `gh api --paginate "repos/{owner}/{repo}/issues/<N>/comments" --jq '.[] | select(.body | contains("<!-- claude-code-review -->")) | .id' | wc -l`.

### Fixing a ready PR

A push to a PR disarms its auto-merge. To push a fix to a ready PR, for example after a CI failure or a late review, first run `gh pr ready <N> --undo`. This cancels in-flight CI, and the fix goes through the lifecycle again: review, promotion, and a new `Enable-AutoMerge.ps1` call for the new head.

## Merge protocol

Auto-merge, armed in step 5 of [draft-review.md](draft-review.md), is the usual way a PR merges.

- A ready PR without auto-merge armed is a merge candidate: arm it. When every check is already terminal and green, you may instead confirm every condition below and merge now with the wrapper.
- `pwsh scripts/Enable-AutoMerge.ps1 -Pr <N>` runs `Assert-PrGreen.ps1 -ForAutoMerge`, which lets CI be pending but refuses a draft, conflicts, a failed check, a review check that is missing or still running, unresolved threads, and a current-head review that is neither clear nor disposed. It then arms a squash merge pinned to the reviewed head, and GitHub merges when the required check passes. When it refuses, fix the reason, or arm on a later visit once the review check completes.
- Merging now with the wrapper requires an open, ready-for-review PR; `MERGEABLE` and `CLEAN`; every applicable check present, terminal, and passing (`SUCCESS`, `SKIPPED`, or `NEUTRAL`, with successful legacy commit statuses); no unresolved threads, unaddressed comments, or outstanding review-body concerns; approval when required; positive verdicts from every required reviewer on the current head; and a bot review/CI cycle after the last fix push. Missing expected checks and nonterminal checks block merging. Reassess if the head changes.
- `Merge-Pr.ps1` calls `Assert-PrGreen.ps1`, merges on a passing mechanical gate, and cleans up. Judge review dispositions and approval yourself.
- Never bypass the wrappers with a direct `gh pr merge` or `gh pr merge --auto`.

## Issue relationships

Native relationships take the REST numeric issue `.id`, not the issue number or `gh issue view --json id` (a GraphQL ID). Pass it with `-F` so it is sent as an integer:

```powershell
$childId = gh api "repos/$repoSlug/issues/<child>" --jq .id
gh api "repos/$repoSlug/issues/<parent>/sub_issues" -X POST -F "sub_issue_id=$childId"

$blockerId = gh api "repos/$repoSlug/issues/<blocker>" --jq .id
gh api "repos/$repoSlug/issues/<blocked>/dependencies/blocked_by" -X POST -F "issue_id=$blockerId"
```

Verify each write with `gh api "repos/$repoSlug/issues/<parent>/sub_issues"` or `gh api "repos/$repoSlug/issues/<blocked>/dependencies/blocked_by"`.
