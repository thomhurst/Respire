# Respire vs Valkey GLIDE benchmarks

`GlideCommonOperationsBenchmarks` compares Respire with the
[Valkey GLIDE](https://github.com/valkey-io/valkey-glide-csharp) C# client
(`Valkey.Glide` package) across common commands: PING, GET (string and long),
SET with 13 B, 1 KB and 10 KB values, 100 sequential GETs and SETs, INCR,
EXISTS, HSET, HGET, LPUSH+LPOP, SADD, SISMEMBER, 50 concurrent GETs and 200
pipelined GETs. Valkey GLIDE is the baseline in each group, so a Ratio below
1.00 means Respire is faster.

Both clients use their default configuration. GLIDE uses its native
`GlideClient` API (one multiplexed connection, RESP3, Rust core over FFI).
Both decode string replies to `string`. GLIDE's SET returns no result, so its
SET cases return `Task`. Setup checks that both clients read the seeded values
before it measures anything.

## Run on GitHub Actions

The `benchmark-valkey-glide.yml` workflow runs on Linux with .NET 10 and a
`valkey/valkey:9.0` container on localhost. Start it in one of two ways:

```sh
# Measure a pull request head (short job)
gh pr edit <number> --add-label run-valkey-glide-benchmarks

# Manual run with a chosen filter and job preset
gh workflow run benchmark-valkey-glide.yml -f job=medium -f filter='*'
```

To measure a later push, remove the label and add it again. Results go to the
job summary. The `valkey-glide-benchmarks-net10.0` artifact holds the
BenchmarkDotNet reports, `project.assets.json` with resolved dependency
versions, and `rig.txt` with the commit, runner, CPU and server identity.

## Run locally

Without `REDIS_HOST`, setup starts a throwaway `valkey/valkey:9.0` container
with Testcontainers, so Docker must be running:

```sh
dotnet run -c Release -f net10.0 --project benchmarks/Respire.GlideComparisonBenchmarks -- --filter '*' --job short
```

Set `REDIS_HOST` and `REDIS_PORT` to use an existing server instead. Use an
isolated server: the suite writes `bench:*` and `seeded:*` keys.
