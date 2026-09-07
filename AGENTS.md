# Repository Guidelines

## Local workload coordination

Before local benchmarks, profiling, stress runs, builds, tests, restores, or other heavy work, follow [the shared performance lock workflow](scripts/PerformanceLock.md). All four repositories reserve the same Redis `performance` key through `C:/git/Dekaf/scripts/AgentLocks.ps1`; this repository's item-lock backend is separate. Reading and editing can continue while another agent owns the reservation.
