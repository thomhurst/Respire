# Local worktree lifecycle

[`AgentLocks.ps1 release`](AgentLocks.ps1) calls [`Remove-ReleasedWorktree`](Remove-ReleasedWorktree.ps1) while the Redis lease is still held, removes its recorded checkout, then releases ownership. Register the path once after checkout with `renew -LockName <name> -Worktree <absolute-path>`. Use the shared checkout's current script, and release in `finally` after stopping owned processes and saving evidence in `<main checkout>-evidence/<lock-name>/` (where agent output goes: [issue-pr-loop § Isolation and ownership](../.claude/skills/issue-pr-loop/SKILL.md#isolation-and-ownership)). `Remove-MergedWorktrees.ps1` deletes evidence entries whose newest file is older than `-EvidenceRetentionDays` (default 7).

The lock marker in a worktree's `config.worktree` records the worktree's own path in `agent.lockWorktree`, because `git worktree add` copies the source checkout's `config.worktree` into the new worktree. A copied marker names another path and is ignored, and the main checkout never receives one.

After a merge, [`Merge-Pr.ps1`](Merge-Pr.ps1) records the remote branch, merged SHA, and a SHA-256 fingerprint of the origin URL in the checkout's Git registration. The marker never stores the URL or embedded credentials. Successful owner release then removes that remote branch through [`MergedBranchCleanup.ps1`](MergedBranchCleanup.ps1), using an expected-head lease and a current origin URL matching the recorded fingerprint. Dirty checkouts retain the marker and remote branch; changed remote tips or origin URLs are preserved. Fork PRs never schedule branch deletion through the base repository's origin. Network failures preserve the remote branch with a warning. Local branch refs remain recovery points.

Local branches remain available for `git worktree add <path> <branch>`. Detached commits are retained as `retained-worktrees/<SHA>`. Uncommitted source, unknown ignored files, Git-locked worktrees, the main checkout and foreign repositories are preserved. Known generated output is disposable under `WorktreeCleanup.ps1`. Release prints why a checkout was retained. On Windows, Git follows directory junctions while removing a worktree and empties their targets, so release first deletes each junction or directory symlink itself and preserves the checkout if any link remains.

Run release with the shared checkout as the working directory. On Windows a process whose current directory is inside the worktree blocks deletion. Git then deletes most of the tree, including its `.git` file, and fails. Release therefore records each vetted checkout under `<git-common-dir>/agent-pending-removals` before deleting it, removes the record on success, and reports a failed removal as pending. Each `Remove-MergedWorktrees.ps1` sweep finishes a pending removal once the item's Redis lock is free, and then applies any deferred remote branch deletion. It deletes only known generated files, one at a time, and empty directories without recursion. It drops a record when a new checkout reuses the path, and preserves the remnant for manual recovery when any other file remains.

A crashed process or an expired Redis TTL cannot run release. `Remove-MergedWorktrees.ps1` remains the conservative fallback for merged work. No GitHub Actions job manages local disk.

Merged-worktree cleanup preserves active Redis ownership, including the current owner's lock, and fails closed when the ownership check fails. It uses the primary checkout's `AgentLocks.ps1`. A merged PR's branch or directory name is not sufficient evidence: the current tip must match a merged PR head or be an eligible snapshot already reachable from `origin/main`. Clean unpublished follow-up commits survive, including with `-StaleDays`. The shared helper rechecks the verified HEAD and ownership before clearing generated output. Explicit lock release remains responsible for removing the checkout.

Unattended sweep and merge cleanup clear only explicitly classified generated output,
including unignored `Debug/` files. They preserve the checkout directory, source, ignored
settings, Git registration, and local branch refs. A free or expired Redis lease does not
prove that a background process released its working directory or directory handles;
renaming the checkout does not stop that process from writing into it. Therefore unattended
cleanup never calls `git worktree remove` or recursively deletes checkout contents.
The owner must stop background processes before explicit release removes its checkout.
If ownership was lost, preserve the checkout for manual recovery after checking its writers.
Concurrent writes inside known generated paths can still race artifact cleanup; that output is rebuildable, while source and unknown ignored paths are never deletion targets.
Orphaned
directories are preserved for manual recovery: a missing registration can hide an explicit
lock name that differs from the directory name, so a free inferred lock is insufficient.
Merge and sweep cleanup retain local branch refs, since a concurrent checkout can reuse
the same branch and commit. Remote branch deletion compares the verified PR head with a lease.

Run regression tests locally with PowerShell 7, Git and Docker:

```powershell
pwsh scripts/Test-ReleasedWorktreeCleanup.ps1
pwsh scripts/Test-AgentLocks.ps1
pwsh scripts/Test-AgentEvidenceCleanup.ps1
pwsh scripts/Test-MergedWorktreeSweep.ps1
pwsh scripts/Test-WorktreeCleanup.ps1
pwsh scripts/Test-WorkflowArtifactCleanup.ps1
pwsh scripts/Test-MergePr.ps1
```
