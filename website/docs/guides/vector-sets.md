# Vector sets

`redis.VectorSets` exposes Redis vector-set commands through typed methods. The wire
contracts and integration tests are pinned to Redis 8.6.0, including RESP2 and RESP3.
Servers without the requested command return their original `RespireServerException`;
Respire does not substitute a search module or another data type.

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

## Inputs and ownership

`AddAsync` and `SearchAsync` accept `ReadOnlyMemory<float>`. The default `Fp32`
encoding writes little-endian components directly into the RESP output buffer without
an intermediate vector-sized byte array or per-component string formatting. This still
copies bytes into the command buffer; it is not zero-copy networking. Command options,
owned replies, and deferred snapshots can allocate. The focused measurements below
quantify encoding only; they do not predict end-to-end client throughput.

Pass `encoding: RespireVectorEncoding.Values` to send invariant decimal components
instead. Empty vectors and nonfinite components are rejected locally. Server checks
still govern dimensions, reduction compatibility, quantization, and filter syntax.
`SearchByMemberAsync` uses `VSIM ELE` and accepts a binary-safe existing member.

Keep immediate-call vector, key, member, and raw attribute memory unchanged until the
operation completes. Batch and transaction methods snapshot these inputs at enqueue
time. Only the Redis key receives the client view's prefix and determines its Cluster
slot; members, filter expressions, and range bounds are literal.

Replies own their arrays and remain valid after client disposal. Arrays are mutable;
record equality compares their references, not contents. Unknown VINFO fields are
owned `RespireResult` values in `AdditionalFields`; they need no disposal, and explicit
disposal invalidates their views. Search results retain server order and binary member
bytes. A null score means scores were not requested. Null attributes can mean either
not requested or absent on that member; retain the request options to distinguish them.

## Options and command mapping

VADD options include `ReduceDimensions` (`REDUCE`, emitted before the vector),
`CheckAndSet` (`CAS`), `Quantization` (`NOQUANT`, `Q8`, or `BIN`), `ExplorationFactor`
(`EF`), raw `AttributesJson` (`SETATTR`), and `Links` (`M`). Null/unspecified options
preserve server defaults. Quantization and reduction affect accuracy and memory;
creation-time settings cannot simply be changed by adding another member.
Redis 8.6 requires subsequent VADD calls to repeat compatible quantization, M, and
REDUCE settings. Omitting them sends server defaults, which can produce a mismatch
error for a set created with nondefault settings. Respire does not discover or remember
per-key creation settings.

VSIM supports result count, scores, attributes, `ExplorationFactor`, `Filter`,
`FilterExplorationFactor`, `Epsilon`, `Exact` (`TRUTH`), and `NoThread` (`NOTHREAD`).
Exact search scans the set; disabling threads can increase server main-thread latency.
Scores are similarity values, not distances. The parser handles RESP2 flat arrays and
RESP3 maps, including nested score/attribute pairs when both are requested.

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

`EmbeddingAsync` returns normalized/reconstructed components; quantization can make
them differ from the original vector. `LinksAsync` returns graph levels from highest
to lowest, optionally with similarity scores. `RandomMembersAsync` uses positive counts
for distinct members and negative counts for sampling with replacement. `long.MinValue`
is rejected to avoid a server-side absolute-count overflow.

`RangeAsync` accepts binary-safe wire bounds: `-` for the start, `+` for the end, and
`[`/`(` prefixes for inclusive/exclusive members. Bounds are not key-prefixed. Count is
a positional argument, not a `COUNT` token; null omits it. Omitted or negative counts
request all matching members on the pinned server, and zero requests none. Bound syntax
is validated by Redis. Unbounded range/random requests and large searches can allocate
large replies; choose counts appropriate to the workload.

## Attribute serialization and deferred commands

Typed attribute helpers always call the configured `IRespireSerializer`, including for
primitive types. That serializer must produce JSON accepted by Redis. A binary or
compression serializer is unsuitable here; use raw JSON helpers or a client configured
with a JSON serializer. Empty raw JSON removes attributes. Missing attributes return
default without invoking deserialization. Serializer trim/AOT annotations still apply.

```csharp
await redis.VectorSets.SetAttributesAsync("products", "sku:1", new { Category = "tools" });
using var batch = redis.CreateBatch();
var count = batch.VectorSets.Count("products");
var matches = batch.VectorSets.SearchByMember("products", "sku:1", new() { Count = 3 });
await batch.ExecuteAsync();
Console.WriteLine(count.Result);
```

Every immediate method has a batch/transaction mirror without `Async` or a per-command
cancellation token. Execute/commit controls cancellation. Pre-cancelled immediate calls
send no command; cancellation after dispatch cannot undo a write. Existing Cluster
routing and redirection behavior applies. Read-only vector commands preserve cached
keyspace values; mutations use the existing invalidation fences. Ordinary metadata
and member-based reads participate in the existing opt-in query cache. Vector-input
searches do not construct cache identities and therefore execute against the server.

**Interface compatibility:** `IRespireClient` and `IRespireCommandQueue` gain a required
`VectorSets` property. External implementations and decorators must implement/forward it.
The generated raw catalog remains available, including specialized options not exposed
by this facet, such as `VEMB RAW`.

Protocol reference: [Redis 8.6 vector-set implementation](https://github.com/redis/redis/blob/8.6.0/modules/vector-sets/vset.c).

## Encoding measurements

[CI run 36700806149](https://github.com/thomhurst/Respire/actions/runs/36700806149)
measured candidate `a98b4750a968ccd5371f0a4d9583a17642b17dfb` between two runs of
baseline `09926d2c50e73986d09f8438fec24a1e854fc87b` on one GitHub-hosted runner.
These point-in-time measurements from September 30, 2026 use the earlier harness revision named above, before
report-gate and documentation fixes. Timed methods and production encoding are unchanged
in those follow-ups. The same benchmark source was copied into the baseline checkout. This compares encoding
alternatives; differences between revisions are runner variation, not a shipped speedup.

Environment: BenchmarkDotNet 0.15.8, Ubuntu 24.04.5, Intel Xeon 6973P-C 2.60 GHz
(2 physical / 4 logical cores exposed), .NET SDK 10.0.401, .NET 10.0.12 X64 RyuJIT,
Concurrent Workstation GC. Each phase used one Default-job launch with adaptive
warmup/measurement iterations. A six-case Dry validation preceded the A/B/A sequence.

| Components | Encoding | Candidate mean +/- StdDev (ns) | Managed B/op | Complete RESP bytes |
| ---: | --- | ---: | ---: | ---: |
| 16 | Direct FP32 | 35.79 +/- 0.53 | 0 | 120 |
| 16 | Intermediate array FP32 | 62.54 +/- 1.00 | 88 | 120 |
| 16 | VALUES | 987.81 +/- 1.82 | 0 | 315 |
| 1,536 | Direct FP32 | 76.30 +/- 0.25 | 0 | 6,202 |
| 1,536 | Intermediate array FP32 | 470.97 +/- 35.63 | 6,168 | 6,202 |
| 1,536 | VALUES | 102,003.89 +/- 152.99 | 0 | 26,026 |

Inputs use seed 534 with finite components in [-1, 1], including positive and negative
zero. All methods encode the complete `VADD vectors ... member` command into equally
preallocated reusable output capacity. Setup checks exact FP32 command bytes, every
component bit, VALUES round-tripping, and capacity. The intermediate path allocates
and copies the vector bytes on every operation; its argument array is prepared outside
timing. The VALUES path formats components directly into the writer, so its measured
0 B/op does not imply low CPU cost.

The two baseline means bracket runner drift as follows (first / second, in ns):
direct FP32 36.16 / 35.57 at 16 components and 78.19 / 77.69 at 1,536; intermediate
FP32 60.15 / 61.34 and 387.48 / 405.25; VALUES 987.80 / 1,035.23 and
103,850.83 / 103,113.66. Allocations and encoded sizes agree across all three phases.
The larger intermediate-array case has substantial timing variation, including its
candidate mean outside both baseline means. Do not treat its exact ratio as stable.
The process could not obtain high scheduling priority on the shared runner.

These results support avoiding the intermediate vector-sized allocation and decimal
formatting for this workload. They exclude immediate-call validation, options/command
construction, deferred snapshots, networking, Redis execution, replies, and concurrency.
Immediate calls still require caller-owned memory to remain unchanged until completion;
batch and transaction calls still allocate owned snapshots at enqueue time. The output
copy remains unavoidable here. No zero-copy networking claim follows from 0 B/op.

The run's `vector-encoding-comparison` artifact retains full BDN JSON, Markdown, and
logs for every phase, including encoded-size evidence and exact revisions. Reproduce
with `benchmarks/Respire.Benchmarks/VectorEncodingBenchmarks.cs` through the focused
`.github/workflows/benchmark-vector.yml` workflow. Evidence is tracked in
[#534](https://github.com/thomhurst/Respire/issues/534) under
[#414](https://github.com/thomhurst/Respire/issues/414).
