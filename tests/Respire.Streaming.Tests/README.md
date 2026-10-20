# Running streaming tests

This executable uses Docker/Testcontainers Redis. Run it from the repository root.
Both `net8.0` and `net10.0` use the same test concurrency policy.

## Concurrency policy

`TestConcurrency.cs` applies TUnit's
[assembly-level `ParallelLimiter`](https://tunit.dev/docs/execution/parallelism/#assembly-level-limiter)
with a limit of **two tests per process**. Streaming tests start hosted workers, use real
deadlines, and share a Redis server. Unrestricted test concurrency can exhaust
those deadlines even when each test has an isolated database. The limit applies
to the whole assembly to reduce unrelated worker tests' competition with the
timing-sensitive scheduling, metrics, and lost-reply tests.

Existing unkeyed `[NotInParallel]` tests still run alone. Tests continue to exercise
concurrent workers and requests internally; the limit controls test cases, not
the concurrency inside a case. Keep existing deadlines and assertions.
Do not add a method or class `ParallelLimiter` that overrides the assembly limit
without revalidating the affected selection under load.

The policy is compiled into the test executable, so IDE runs, `dotnet test`,
`dotnet run`, and direct executable launches use it. CI's component jobs in
`ci.yml`, the full .NET 8 job in `test-full-net8.yml`, and the Redis/Valkey
selections inherit it without extra command-line flags. CI's `SUITE_MAX_PARALLEL`
separately bounds the number of test **processes** running on the agent.

## Local regression selection

Build first, then run the regression selection using the repository's resource
guard:

```powershell
& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'build', 'tests/Respire.Streaming.Tests/Respire.Streaming.Tests.csproj',
    '-c', 'Release', '-f', 'net10.0')

& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'test', '--project', 'tests/Respire.Streaming.Tests/Respire.Streaming.Tests.csproj',
    '-c', 'Release', '-f', 'net10.0', '--no-build', '--treenode-filter',
    '/*/*/(JobCheckpointIntegrationTests)|(JobSchedulingIntegrationTests)|(JobQueueIntegrationTests)|(JobGroupControlIntegrationTests)|(StreamWorkerTests)|(StreamWorkerIntegrationTests)|(JobTransactionIntegrationTests)/*')
```

`JobQueueIntegrationTests` includes the partial classes in
`JobMetricsIntegrationTests.cs` and `JobQueueEventIntegrationTests.cs`.
`StreamWorkerTests` likewise includes `StreamWorkerDeadLetterTests.cs`.
Remove `--treenode-filter` and its value to run the full executable. Substitute
`net8.0` in both commands for the other supported framework.
For parity with CI's component jobs, set
`$env:TUNIT_DISABLE_HTML_REPORTER = 'true'` before running the tests.

For a sequential diagnostic run, append `--maximum-parallel-tests 1`.
Passing a larger runner maximum does not bypass the assembly limiter.

## Evidence

[Issue #1938](https://github.com/thomhurst/Respire/issues/1938) records a Windows
.NET 10 / Redis 7.0.15 comparison: the same 384-test binaries produced 13 timeout
failures with unrestricted concurrency and passed all 384 tests with
`--maximum-parallel-tests 2`, while a guarded dashboard build was also active.
The issue and its closing PR record the follow-up baseline and compiled-policy
runs. This identifies aggregate test-concurrency pressure; it does not establish
whether CPU, thread-pool scheduling, or Redis was the individual bottleneck.
The bound is per process and cannot prevent contention from other test processes
or unrelated machine load.
