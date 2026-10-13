# Generated mapper Native AOT sample

This executable exercises generated hash, JSON and Search mappers against Redis 8.10 with
the JSON and Search modules. Each run uses unique keys and indexes and deletes its data.
It checks both RESP2 and RESP3, scalar/null codecs, both hash field expiry modes, tracked
partial updates, hash and JSON partial reads, generated Search schemas and vector queries.
A failed assertion exits with an error.

Use the repository's .NET SDK from `global.json` to compile its C# language features.
The sample targets both `net8.0` and `net10.0`; publishing selects the corresponding
Native AOT runtime toolchain. Native AOT also requires the platform's native compiler.
Run these commands from the repository root.

Start Redis 8.10, which bundles the JSON and Search modules (Docker with Linux containers):

```sh
docker run --detach --rm --name respire-mapper-sample-redis --publish 127.0.0.1:6379:6379 redis:8.10-alpine
```

Publish and run the sample. The examples use `linux-x64`; pass your own runtime
identifier (for example `win-x64` or `osx-arm64`) on other platforms. The only
argument is the connection string, which defaults to `127.0.0.1:6379`.

```sh
dotnet publish samples/Respire.Samples.Mappers -c Release -f net8.0 -r linux-x64 -o artifacts/mapper-net8
artifacts/mapper-net8/Respire.Samples.Mappers redis://127.0.0.1:6379
dotnet publish samples/Respire.Samples.Mappers -c Release -f net10.0 -r linux-x64 -o artifacts/mapper-net10
artifacts/mapper-net10/Respire.Samples.Mappers redis://127.0.0.1:6379
```

A successful run prints one line per protocol:

```text
Generated hash/JSON/Search mapper conformance passed (Resp2).
Generated hash/JSON/Search mapper conformance passed (Resp3).
```

Stop Redis with `docker stop respire-mapper-sample-redis`.

The project enables trimming and Native AOT, treats compiler and linker warnings as
errors, and disables reflection-based JSON serialization. Generated `JsonTypeInfo`
metadata must suffice for all JSON operations. A negative control verifies that an
unmapped type cannot obtain metadata from an empty resolver; no reflection fallback is
installed. Generator conformance tests separately reject reflection, dynamic binding,
trim/dynamic-code annotated APIs and JSON serializer calls without explicit metadata.

`--codecs-only` runs scalar/null codec and metadata controls without Redis. It does not
validate Redis operations. The `Generated mapper Native AOT` CI jobs publish and execute
the complete sample for both frameworks on Linux. This is correctness coverage; mapper
performance is measured separately by the `benchmark-mapper.yml` workflow.
