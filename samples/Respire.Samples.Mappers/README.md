# Generated mapper Native AOT sample

This executable exercises generated hash, JSON and Search mappers against Redis 8.10 with
the JSON and Search modules. Each run uses unique keys and indexes and deletes its data.
It checks both RESP2 and RESP3, scalar/null codecs, both hash field expiry modes, tracked
partial updates, hash and JSON partial reads, generated Search schemas and vector queries.
A failed assertion exits with an error.

Use the repository's .NET SDK from `global.json` to compile its C# language features.
The sample targets both `net8.0` and `net10.0`; publishing selects the corresponding
Native AOT runtime toolchain. Native AOT also requires the platform's native compiler.

```sh
dotnet publish samples/Respire.Samples.Mappers -c Release -f net8.0 -r linux-x64 -o artifacts/mapper-net8
artifacts/mapper-net8/Respire.Samples.Mappers redis://127.0.0.1:6379
dotnet publish samples/Respire.Samples.Mappers -c Release -f net10.0 -r linux-x64 -o artifacts/mapper-net10
artifacts/mapper-net10/Respire.Samples.Mappers redis://127.0.0.1:6379
```

The project enables trimming and Native AOT, treats compiler and linker warnings as
errors, and disables reflection-based JSON serialization. Generated `JsonTypeInfo`
metadata must suffice for all JSON operations. A negative control verifies that an
unmapped type cannot obtain metadata from an empty resolver; no reflection fallback is
installed. Generator conformance tests separately reject reflection, dynamic binding,
trim/dynamic-code annotated APIs and JSON serializer calls without explicit metadata.

`--codecs-only` runs scalar/null codec and metadata controls without Redis. It does not
validate Redis operations. The `Generated mapper Native AOT` CI jobs publish and execute
the complete sample for both frameworks on Linux. This is correctness coverage; mapper
benchmark parity remains separately tracked in #1301 and #1254.
