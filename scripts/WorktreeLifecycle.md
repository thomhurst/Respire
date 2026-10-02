# Local worktree lifecycle

`AgentLocks.ps1 release` removes its recorded checkout before releasing ownership. Register the path once after checkout with `renew -LockName <name> -Worktree <absolute-path>`. Use the shared checkout's current script, and release in `finally` after stopping owned processes and saving evidence outside the worktree.

Local branches remain available for `git worktree add <path> <branch>`. Detached commits are retained as `retained-worktrees/<SHA>`. Uncommitted source, unknown ignored files, Git-locked worktrees, the main checkout and foreign repositories are preserved. Known generated output is disposable under `WorktreeCleanup.ps1`. Release prints why a checkout was retained.

A crashed process or an expired Redis TTL cannot run release. `Remove-MergedWorktrees.ps1` remains the conservative fallback for merged work. No GitHub Actions job manages local disk.

Merged-worktree cleanup preserves active Redis ownership, including the current owner's lock, and fails closed when the ownership check fails. It uses the primary checkout's `AgentLocks.ps1`. A merged PR's branch or directory name is not sufficient evidence: the current tip must match a merged PR head or be an eligible snapshot already reachable from `origin/main`. Clean unpublished follow-up commits survive, including with `-StaleDays`. The shared removal helper rechecks the verified HEAD and ownership before deletion. Merge cleanup can leave an owned checkout for the normal explicit lock release to remove.

The sweep moves a completed checkout to a unique sibling quarantine path, then rechecks
HEAD, ownership, and uncommitted/ignored files before ordinary `git worktree remove`.
This captures late writes at the original path before the move and preserves any files
created at that path afterward. Retained snapshots move back only if the original path
is still free; otherwise the reported quarantine path is available for recovery. Agents
must still hold ownership while working and stop their background processes before release.
Known generated files such as `Debug/` output are cleared even when Git does not ignore them.
The sweep never forces removal or falls back to recursive deletion after Git refuses. Orphaned
directories are preserved for manual recovery: a missing registration can hide an explicit
lock name that differs from the directory name, so a free inferred lock is insufficient.
Merge and sweep cleanup retain local branch refs, since a concurrent checkout can reuse
the same branch and commit. Remote branch deletion compares the verified PR head with a lease.

Run regression tests locally with PowerShell 7, Git and Docker:

```powershell
pwsh scripts/Test-ReleasedWorktreeCleanup.ps1
pwsh scripts/Test-MergedWorktreeSweep.ps1
pwsh scripts/Test-WorktreeCleanup.ps1
pwsh scripts/Test-WorkflowArtifactCleanup.ps1
pwsh scripts/Test-MergePr.ps1
```
