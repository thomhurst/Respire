# Vector sets

`VectorSets` on `IRespireClient` exposes Redis vector-set commands (`VADD`, `VSIM` and related) as typed methods. Use it for native vector similarity on a single key without a Search index. The wire format follows Redis 8.6.0 over RESP2 and RESP3. Servers without the command return their original `RespireServerException`. The examples use `redis` for a connected `RespireClient`.

```csharp
float[] embedding = [1, 0, 0];
await redis.VectorSets.AddAsync("products", embedding, "sku:1", new()
{
    Quantization = RespireVectorQuantization.None,
    AttributesJson = "{\"category\":\"tools\"}",
});
var matches = await redis.VectorSets.SearchAsync("products", embedding, new()
{
    Count = 10,
    IncludeScores = true,
    IncludeAttributes = true,
    Filter = ".category == \"tools\"",
});
foreach (var match in matches)
    Console.WriteLine($"{Convert.ToHexString(match.Member)}: {match.Score}");
```

`AddAsync` returns `false` when it replaces an existing member: no new member was inserted, but the replacement succeeded.

:::warning Replacement durability
On Redis **8.6.0** and **8.10.2**, acknowledged `VADD` replacements lose their new vector and attributes after an AOF crash/reload. Lua-wrapped `VADD` has the same problem. Respire forwards the native command unchanged. See the [VectorData backend decision](vector-data.md#vector-set-backend-decision) before relying on durable replacement.
:::

## Inputs and ownership

`AddAsync` and `SearchAsync` take `ReadOnlyMemory<float>`.

- The default `Fp32` encoding writes little-endian components straight into the command buffer, with no intermediate array.
- `encoding: RespireVectorEncoding.Values` sends invariant decimal components instead.
- Empty vectors and nonfinite components are rejected locally. Redis checks dimensions, reduction, quantization and filter syntax.
- `SearchByMemberAsync` uses `VSIM ELE` with a binary-safe existing member.
- Keep vector, key, member and attribute memory unchanged until an immediate call completes. Batches and transactions snapshot inputs at enqueue time.
- Only the key gets the client's key prefix and decides the Cluster slot. Members, filters and range bounds are literal.

Replies:

- Replies own their arrays and stay valid after client disposal. Arrays are mutable, and record equality compares references, not contents.
- Unknown VINFO fields are owned `RespireResult` values in `AdditionalFields`. They need no disposal; disposing them invalidates their views.
- Search results keep server order and binary member bytes.
- A null score means scores were not requested. A null attribute means not requested or absent; check your request options to tell which.

## Options and command mapping

VADD options (null keeps the server default):

| Option | Token |
| --- | --- |
| `ReduceDimensions` | `REDUCE` (sent before the vector) |
| `CheckAndSet` | `CAS` |
| `Quantization` | `NOQUANT`, `Q8` or `BIN` |
| `ExplorationFactor` | `EF` |
| `AttributesJson` | `SETATTR` (raw JSON) |
| `Links` | `M` |

**Repeat creation settings on every VADD.** Quantization and reduction are fixed when the set is created. Redis 8.6 requires later VADD calls to send compatible quantization, `M` and `REDUCE`. Omitting them sends server defaults, which fails on a set created with other settings. Respire does not remember per-key settings.

VSIM options: result count, scores, attributes, `ExplorationFactor`, `Filter`, `FilterExplorationFactor`, `Epsilon`, `Exact` (`TRUTH`) and `NoThread` (`NOTHREAD`). Exact search scans the whole set. `NoThread` can increase latency on the server's main thread. Scores are similarities, not distances.

| Method | Command | Missing result |
| --- | --- | --- |
| `AddAsync` | VADD | Creates the set; true means a new member |
| `RemoveAsync` | VREM | false |
| `CountAsync` | VCARD | 0 |
| `DimensionsAsync` | VDIM | Server error |
| `EmbeddingAsync` | VEMB | null |
| `ContainsAsync` | VISMEMBER | false |
| `GetAttributesJsonAsync`, `GetAttributesAsync<T>` | VGETATTR | null/default |
| `SetAttributesJsonAsync`, `SetAttributesAsync<T>` | VSETATTR | false |
| `InfoAsync` | VINFO | null |
| `LinksAsync` | VLINKS | null |
| `RandomMemberAsync` | VRANDMEMBER | null |
| `RandomMembersAsync` | VRANDMEMBER count | Empty array |
| `RangeAsync` | VRANGE | Empty array |
| `SearchAsync`, `SearchByMemberAsync` | VSIM | Missing key: empty array; missing ELE member: server error |

- `EmbeddingAsync` returns normalized or reconstructed components; quantization can make them differ from the original.
- `LinksAsync` returns graph levels from highest to lowest, optionally with similarity scores.
- `RandomMembersAsync`: a positive count returns distinct members; a negative count samples with replacement. `long.MinValue` is rejected.
- `RangeAsync` bounds: `-` for start, `+` for end, `[`/`(` prefix for inclusive/exclusive members. Bounds are binary-safe, not key-prefixed and validated by Redis. Count is positional (no `COUNT` token). Null or negative requests all matches; zero requests none.
- Unbounded range/random requests and large searches can return large replies. Set counts to suit the workload.

## Attribute serialization and deferred commands

Typed attribute helpers always use the configured `IRespireSerializer`, even for primitives, and Redis must accept its output as JSON. Do not use a binary or compressing serializer here; use the raw JSON helpers instead. Empty raw JSON removes attributes. Missing attributes return default without calling the serializer. Serializer trim/AOT annotations still apply.

```csharp
await redis.VectorSets.SetAttributesAsync("products", "sku:1", new { Category = "tools" });
using var batch = redis.CreateBatch();
var count = batch.VectorSets.Count("products");
var matches = batch.VectorSets.SearchByMember("products", "sku:1", new() { Count = 3 });
await batch.ExecuteAsync();
Console.WriteLine(count.Result);
```

- Each immediate method has a batch/transaction mirror without `Async` or a per-command cancellation token. Execute/commit controls cancellation.
- A pre-cancelled immediate call sends nothing. Cancelling after dispatch cannot undo a write.
- Normal Cluster routing and redirection apply.
- Client-side cache: reads keep cached values; writes invalidate the key. Metadata and member-based reads use the opt-in query cache. Vector-input searches always go to the server.
- The raw command catalog covers options this facet omits, such as `VEMB RAW`.

**Interface implementers:** `IRespireClient` and `IRespireCommandQueue` declare `VectorSets` with no default implementation. Custom implementations and decorators must implement or forward it.

Protocol reference: [Redis 8.6 vector-set implementation](https://github.com/redis/redis/blob/8.6.0/modules/vector-sets/vset.c).

## Encoding measurements

BenchmarkDotNet results from September 30, 2026, on one GitHub-hosted runner. They compare encoding alternatives within one build, not releases, and cover encoding only, not end-to-end throughput. The run used an A/B/A sequence with identical production code; the table shows the middle (candidate) phase.

Environment: BenchmarkDotNet 0.15.8, Ubuntu 24.04.5, Intel Xeon 6973P-C 2.60 GHz (2 physical / 4 logical cores exposed), .NET SDK 10.0.401, .NET 10.0.12 X64 RyuJIT, Concurrent Workstation GC.

| Components | Encoding | Candidate mean +/- StdDev (ns) | Managed B/op | Complete RESP bytes |
| ---: | --- | ---: | ---: | ---: |
| 16 | Direct FP32 | 35.79 +/- 0.53 | 0 | 120 |
| 16 | Intermediate array FP32 | 62.54 +/- 1.00 | 88 | 120 |
| 16 | VALUES | 987.81 +/- 1.82 | 0 | 315 |
| 1,536 | Direct FP32 | 76.30 +/- 0.25 | 0 | 6,202 |
| 1,536 | Intermediate array FP32 | 470.97 +/- 35.63 | 6,168 | 6,202 |
| 1,536 | VALUES | 102,003.89 +/- 152.99 | 0 | 26,026 |

Baseline means, first / second phase (ns): direct FP32 36.16 / 35.57 at 16 components and 78.19 / 77.69 at 1,536; intermediate FP32 60.15 / 61.34 and 387.48 / 405.25; VALUES 987.80 / 1,035.23 and 103,850.83 / 103,113.66.

- Each case encodes the full `VADD vectors ... member` command into preallocated output. Inputs use seed 534 with finite components in [-1, 1].
- The 1,536-component intermediate-array timing varies widely; do not treat its ratio as stable.
- VALUES shows 0 B/op but costs the most CPU.
- Measurements exclude validation, command construction, batch snapshots, networking, Redis and replies. 0 B/op does not mean zero-copy networking.

Reproduce with `benchmarks/Respire.Benchmarks/VectorEncodingBenchmarks.cs` through the `.github/workflows/benchmark-vector.yml` workflow.
