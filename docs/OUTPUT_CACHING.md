# Output caching maintainer notes

How to validate and measure the `Respire.OutputCaching` store. When you rerun the tagging probe,
update the performance table on the website. The user guide is
[Output caching](https://respire.tomlonghurst.dev/docs/integrations/output-caching).

## Validation tests

`tests/Respire.Caching.Tests/OutputCacheTests.cs` covers buffer reads, segmented writes, master-tag
score monotonicity, eviction, expired-tag cleanup, cleanup-lock contention, cancellation,
registration, and bidirectional Microsoft-store use.

## Tagging probe

`tools/OutputCacheTaggingProbe` measures, for both tagging modes: serial sets/gets, one full-tag
eviction, expired reference cleanup, and Redis `MEMORY USAGE`.

- 500 values per dataset, 1 KiB payloads, three rounds, alternating mode order, unique namespaces.
- Warmup loads the scripts before timing.
- After deleting values, it advances only the cleanup clock, so it prunes references without waiting
  ten minutes.
- It never scans or flushes a whole database, and leaves its database empty on success.

Run it against a disposable Redis instance:

```powershell
& scripts/Invoke-AgentDotNet.ps1 -DotNetArguments @('run', '--project', 'tools/OutputCacheTaggingProbe', '-f', 'net10.0', '--', 'redis://localhost:6379')
```

The website table comes from a 2026-10-05 development run: Linux .NET 10.0.12 / SDK 10.0.401 and
Redis 7.0.15 on Docker Desktop, one CPU per client/server container, 2 GiB client and 256 MiB server
memory, on a host doing other work. Each cell is the median of three dataset results; set/get results
are averages within a dataset. Tag-index size varies with Redis's allocation and skip-list layout.
