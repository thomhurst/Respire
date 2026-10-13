The user guide for `Respire.OutputCaching` is on the website: [Output caching](https://thomhurst.github.io/Respire/docs/integrations/output-caching). This file keeps maintainer notes for validating and measuring the store.

# Output caching maintainer notes

## Validation tests

Validation scenarios live in `tests/Respire.Caching.Tests/OutputCacheTests.cs`. They cover
buffer reads, segmented writes, master-tag score monotonicity, eviction, expired-tag cleanup,
cleanup-lock contention, cancellation, registration, and bidirectional Microsoft-store use.

## Tagging probe

`tools/OutputCacheTaggingProbe` measures serial sets/gets, one full-tag eviction, expired
reference cleanup, and Redis `MEMORY USAGE` for both tagging modes. It uses unique
namespaces, 500 values per dataset, 1 KiB payloads, three rounds, and alternating mode
order. Warmup loads the scripts before timing. After deleting values, it advances only the
cleanup clock to prune remaining references without waiting ten minutes. It never scans or
flushes an entire database, and it leaves its Redis database empty on successful completion.
Run it against a disposable Redis instance:

```powershell
& scripts/Invoke-AgentDotNet.ps1 -DotNetArguments @('run', '--project', 'tools/OutputCacheTaggingProbe', '-f', 'net10.0', '--', 'redis://localhost:6379')
```

The website's performance table comes from a 2026-10-05 development run: Linux .NET 10.0.12 /
SDK 10.0.401 and Redis 7.0.15 on Docker Desktop, with one CPU per client/server container,
2 GiB client memory and 256 MiB server memory. The host also ran other development work.
Each cell is the median of three dataset results; set/get results are averages within a
dataset. Tag-index size varies with Redis's internal allocation and skip-list layout.
Update the website table when you rerun the probe.
