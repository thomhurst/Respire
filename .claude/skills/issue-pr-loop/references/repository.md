# Respire

- Every local .NET command uses `scripts/Invoke-AgentDotNet.ps1`, invoked in-process in PowerShell (`& scripts/Invoke-AgentDotNet.ps1 -DotNetArguments @('build')`). Allow the outer timeout at least 30 seconds beyond the guard; exit 124/137 is a validation limit, not permission to raise limits.
- Run relevant TUnit executable projects under `tests/`, selecting a supported framework and `--treenode-filter` for focus. Prefer `Respire.Tests`/`Respire.Pipeline.Tests` for fast feedback; integration tests require Docker/Testcontainers Redis. Website changes require `npm run build --prefix website`.
- Library awaits use `ConfigureAwait(false)`. Never hand-edit `src/Respire/RespireCommands.g.cs`; change `tools/Generate-CommandCatalog.ps1` or an appropriate manual partial/extension. Review/simplification must respect that boundary.
- Preserve networking FIFO response order, buffer reference counts, uninterrupted in-flight writes, and the persistent flush task.
- Never run the full benchmark suite as an agent; benchmark CI owns performance validation. Report unavailable Docker validation.
- No Aspire AppHost. Testcontainers owns its ephemeral Redis lifecycle; preserve shared lock Redis and peers' resources.
