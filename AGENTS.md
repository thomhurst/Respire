# Repository Guidelines

## Release notes

Do not create or maintain a release notes or changelog file (for example `docs/RELEASE_NOTES.md` or `CHANGELOG.md`). GitHub generates release notes from merged PRs, and a shared file causes merge conflicts between PRs. When a rebase or merge hits a conflict on `docs/RELEASE_NOTES.md`, resolve it by deleting the file (`git rm docs/RELEASE_NOTES.md`). Never restore it. Describe user-facing changes in the PR title and description instead.

## Source escape sequences

Several PR builds failed with `CS1010: Newline in constant` because a tool expanded `\r\n` inside a C# string literal into real line breaks before the file was written. Write source with file-editing tools, or with a quoted heredoc (`<<'EOF'`). Do not use `echo -e`, `printf` format strings, interpolated PowerShell strings, or non-raw string literals in a generating script for this. Build the changed projects before you push.

## Allocation tests

Zero-allocation assertions must use `AllocationMeasurement.WithoutConcurrentGc` with an unkeyed `[NotInParallel]` test, warmed no-inline measurement methods, and a positive control. A bare `GC.GetAllocatedBytesForCurrentThread()` delta is flaky on CI. See `docs/ALLOCATION_MEASUREMENT.md`.

## Pull request reviews

- Resolve each PR review thread, whether a human or a bot opened it, as soon as you have dispositioned it: the fix is pushed to the PR head and your reply names the commit, or your reply pushes back on the finding with evidence. Leave a thread open only while it has no disposition. If the reviewer replies after your disposition, unresolve the thread and handle the reply.
