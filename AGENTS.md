# Repository Guidelines

## Local workload coordination

Before local benchmarks, profiling, stress runs, builds, tests, restores, or other heavy work, follow [the shared performance lock workflow](scripts/PerformanceLock.md). All four repositories reserve the same Redis `performance` key through `C:/git/Dekaf/scripts/AgentLocks.ps1`; this repository's item-lock backend is separate. Reading and editing can continue while another agent owns the reservation.

## Release notes

Do not create or maintain a release notes or changelog file (for example `docs/RELEASE_NOTES.md` or `CHANGELOG.md`). GitHub generates release notes from merged PRs, and a shared file causes merge conflicts between PRs. When a rebase or merge hits a conflict on `docs/RELEASE_NOTES.md`, resolve it by deleting the file (`git rm docs/RELEASE_NOTES.md`). Never restore it. Describe user-facing changes in the PR title and description instead.
