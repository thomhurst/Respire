# Server diagnostics

Use `redis.Server` to read latency reports, latency histories and histograms, memory reports, and the slow-log length, and to purge allocator memory. Each ordinary method runs on one node.

```csharp
string latencyReport = await redis.Server.LatencyDoctorAsync();
RespireLatencyHistorySample[] history = await redis.Server.LatencyHistoryAsync("command");
RespireLatencyHistogram[] histograms = await redis.Server.LatencyHistogramsAsync(["get", "set"]);
string memoryReport = await redis.Server.MemoryDoctorAsync();
long slowLogLength = await redis.Server.SlowLogLengthAsync();
```

- Event and command names are sent literally; `WithKeyPrefix` never changes them.
- These methods are immediate, with no batch or transaction versions.
- For the latest latency events, memory statistics, and slow-log entries, use `LatestLatencyAsync`, `MemoryStatsAsync`, and `SlowLogAsync`.
- `SlowLogLengthAsync` counts retained entries, not every command that ever exceeded the threshold.
- Doctor methods return the server's human-readable text, including RESP3 verbatim strings. The wording varies by version and configuration; do not parse it. Use the typed measurements for monitoring.

## Histories and histograms

Latency history:

- Needs a nonzero `latency-monitor-threshold` (milliseconds).
- Keeps at most 160 samples per event. Unknown events return an empty array.
- `Timestamp` is a `DateTimeOffset` (from Unix seconds); `Latency` is a `TimeSpan` (from integer milliseconds).
- Event names cannot be null or whitespace.

Latency histograms:

- Need `latency-tracking`, on by default on supported servers.
- `Calls` is the number of sampled calls. Each bucket has an upper bound in **microseconds** and a **cumulative** count; subtract the previous bucket's count to get one bucket's count.
- Counts accumulate until `CONFIG RESETSTAT`. They are separate from the event monitor and `LATENCY RESET`. These methods never change either configuration.
- Omit names to get every histogram. The server omits unknown names and commands without samples, may expand a parent command to subcommands, and returns its own names (such as `config|get`).
- Names are copied before async work and sent as separate arguments without splitting. Null names are rejected.
- Repeated or overlapping names can produce duplicate results, which are kept.

Results:

- Results own their data and survive reply and client disposal. Arrays are mutable; record equality compares array references.
- Histogram `AdditionalFields` keeps future fields as `RespireResult` values. Disposing them is optional and invalidates their nested views.
- Missing required fields, impossible times, and invalid cumulative buckets throw `RespireProtocolException`.
- Snapshots allocate. With large command catalogs or many nodes, request specific commands and avoid overlapping polls.

## Memory purging

`PurgeMemoryAsync` requires `RespireOptions.AllowAdmin = true`, checked before any I/O. Server ACL checks apply to every diagnostic command.

```csharp
await using var admin = RespireClient.Create(new RespireOptions
{
    Endpoints = [new RespireEndpoint("localhost", 6379)],
    AllowAdmin = true,
});
await admin.Server.PurgeMemoryAsync();
```

- `MEMORY PURGE` asks jemalloc to release reclaimable pages and can block the server. Other allocators may treat it as a no-op.
- Success does not guarantee lower resident memory.
- It does not delete keys or flush Respire's client-side cache. The read-only diagnostics do not affect the cache either.
- Cancellation cannot undo a purge the server accepted.

## Explicit per-node results

All six methods have `OnAllNodesAsync` counterparts that return `RespireServerResult<T>[]`. They follow the [server fan-out contract](pub-sub-introspection.md#results-from-every-node): fresh Cluster discovery including replicas, temporary connections with bounded concurrency, and a value or failure per endpoint. Standalone returns one result. Discovery failure or cancellation throws; after discovery, cancellation is recorded per node. Check `IsSuccess` or `Error` before `Value`.

```csharp
var nodes = await redis.Server.LatencyHistogramsOnAllNodesAsync(["get"]);
foreach (var node in nodes)
{
    if (node.IsSuccess)
        Console.WriteLine($"{node.Endpoint}: {node.Value.Length} histogram(s)");
    else
        Console.WriteLine($"{node.Endpoint}: {node.Error}");
}
```

- Snapshots are not atomic or aggregated. Different windows and resets can make cumulative counts incomparable.
- Fan-out never changes slot ownership or retries on another endpoint.
- `PurgeMemoryOnAllNodesAsync` also requires `AllowAdmin`, returns `true` per successful endpoint, and can partly succeed. A purge cannot be rolled back.

## Compatibility

| API pair (ordinary and `OnAllNodesAsync`) | Command | Minimum Redis version |
| --- | --- | --- |
| `LatencyDoctorAsync` | LATENCY DOCTOR | 2.8.13 |
| `LatencyHistoryAsync` | LATENCY HISTORY | 2.8.13 |
| `LatencyHistogramsAsync` | LATENCY HISTOGRAM | 7.0 |
| `MemoryDoctorAsync` | MEMORY DOCTOR | 4.0 |
| `PurgeMemoryAsync` | MEMORY PURGE | 4.0 |
| `SlowLogLengthAsync` | SLOWLOG LEN | 2.2.12 |

- Valkey 7.2.5+ supports all six commands (see its [command metadata](https://github.com/valkey-io/valkey/tree/7.2.5/src/commands)).
- Tested on Redis 6.2, Redis 7.0, and Valkey 8.1 with RESP2 and RESP3.
- Unsupported commands and ACL denials return the server error; there is no fallback.
- The minimum versions apply to the commands only; not every Respire connection or fan-out feature works on old servers.
- The six methods and their six `OnAllNodesAsync` counterparts are `IRespireServerCommands` members. Custom implementations, decorators, and mocks must implement or forward them.

References: Redis [LATENCY DOCTOR](https://redis.io/docs/latest/commands/latency-doctor/),
[LATENCY HISTORY](https://redis.io/docs/latest/commands/latency-history/),
[LATENCY HISTOGRAM](https://redis.io/docs/latest/commands/latency-histogram/),
[MEMORY DOCTOR](https://redis.io/docs/latest/commands/memory-doctor/),
[MEMORY PURGE](https://redis.io/docs/latest/commands/memory-purge/), and
[SLOWLOG LEN](https://redis.io/docs/latest/commands/slowlog-len/);
Valkey [histograms](https://valkey.io/commands/latency-histogram/),
[history](https://valkey.io/commands/latency-history/), and
[memory purging](https://valkey.io/commands/memory-purge/).
