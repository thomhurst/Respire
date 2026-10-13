# Generated mapper Native AOT sample

A Native AOT executable that checks generated hash, JSON, and Search mappers
against Redis 8.10 on RESP2 and RESP3. It covers codecs, hash field expiry,
partial updates and reads, generated Search schemas, and vector queries. Each run
uses unique keys and indexes, deletes its data, and exits with an error on any
failure.

You need the .NET SDK from `global.json`, Docker with Linux containers, and your
platform's native compiler for Native AOT. The sample targets `net8.0` and
`net10.0`. Run commands from the repository root.

Start Redis 8.10 (it bundles JSON and Search):

```sh
docker run --detach --rm --name respire-mapper-sample-redis --publish 127.0.0.1:6379:6379 redis:8.10-alpine
```

Publish and run. Replace `linux-x64` with your runtime identifier (for example
`win-x64` or `osx-arm64`). The only argument is the connection string (default
`127.0.0.1:6379`).

```sh
dotnet publish samples/Respire.Samples.Mappers -c Release -f net8.0 -r linux-x64 -o artifacts/mapper-net8
artifacts/mapper-net8/Respire.Samples.Mappers redis://127.0.0.1:6379
dotnet publish samples/Respire.Samples.Mappers -c Release -f net10.0 -r linux-x64 -o artifacts/mapper-net10
artifacts/mapper-net10/Respire.Samples.Mappers redis://127.0.0.1:6379
```

A successful run prints:

```text
Generated hash/JSON/Search mapper conformance passed (Resp2).
Generated hash/JSON/Search mapper conformance passed (Resp3).
```

Stop Redis with `docker stop respire-mapper-sample-redis`.

The project enables trimming and Native AOT, treats compiler and linker warnings
as errors, and disables reflection-based JSON serialization, so generated
`JsonTypeInfo` metadata must cover every JSON operation.

`--codecs-only` runs the codec and metadata checks without Redis; it does not
test Redis operations. Mapper performance is measured by `benchmark-mapper.yml`.
