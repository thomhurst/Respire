# Server diagnostics

`redis.Server` exposes latency reports and histories, cumulative command latency
histograms, memory reports and allocator purging, and the current slow-log length.
Each ordinary method executes on one node. Event and command names remain literal;
`WithKeyPrefix` never changes them. These methods are immediate and have no deferred
batch or transaction counterparts.

```csharp
string latencyReport = await redis.Server.LatencyDoctorAsync();
RespireLatencyHistorySample[] history = await redis.Server.LatencyHistoryAsync("command");
RespireLatencyHistogram[] histograms = await redis.Server.LatencyHistogramsAsync(["get", "set"]);
string memoryReport = await redis.Server.MemoryDoctorAsync();
long slowLogLength = await redis.Server.SlowLogLengthAsync();
```

The existing `LatestLatencyAsync`, `MemoryStatsAsync`, and `SlowLogAsync` methods keep
their existing contracts. `SlowLogLengthAsync` counts retained entries, not all commands
that have ever exceeded the configured slow-log threshold.

## Histories and histograms

Event history requires a nonzero `latency-monitor-threshold` configuration, measured
in milliseconds. Each event retains at most 160 samples. Unknown events return an empty
array. `Timestamp` converts Unix seconds to `DateTimeOffset`; `Latency` converts the
server's integer milliseconds to `TimeSpan`. Event names cannot be null or whitespace.

Histograms require `latency-tracking`, enabled by default on supported servers.
`Calls` is the number of sampled calls, and every bucket contains an upper bound in
**microseconds** with a **cumulative** count. Subtract the previous bucket's count to
obtain an individual bucket's count. These distributions accumulate across calls;
`CONFIG RESETSTAT` clears them. They are separate from the event monitor and its
`LATENCY RESET` command. These inspection methods do not change either configuration.

Omit command names to request every available histogram. Unknown names and commands
without samples are omitted by the server. Names are copied before asynchronous work
and sent as individual arguments without tokenization. Null names are rejected.
The server may expand a parent command to its subcommands and returns its own command
names, including names such as `config|get`. Repeated or overlapping requested names
can produce duplicate results; the returned array preserves them.

Results own their strings, arrays, and nested unknown fields and survive reply or client
disposal. Arrays are mutable; record equality compares array references. Histogram
`AdditionalFields` preserves future fields as recursively copied `RespireResult` values
backed by GC-owned storage. Disposal of those values is optional; explicit disposal
invalidates their nested views. Required malformed fields, impossible time values, and
invalid cumulative bucket sequences produce `RespireProtocolException`.

## Memory purging

`PurgeMemoryAsync` requires `RespireOptions.AllowAdmin = true`, checked before network
I/O. Server ACL checks still apply to every diagnostic command.

```csharp
await using var admin = RespireClient.Create(new RespireOptions
{
    Endpoints = [new RespireEndpoint("localhost", 6379)],
    AllowAdmin = true,
});
await admin.Server.PurgeMemoryAsync();
```

`MEMORY PURGE` asks jemalloc to release reclaimable pages and can block the server.
Other allocators may acknowledge it as a no-op. Success does not promise a reduction
in resident memory. It does not delete keys or flush Respire's client-side cache;
the read-only diagnostics also preserve cached values and in-flight cache reads.
Cancellation cannot undo a purge already accepted by the server.

Both doctor methods return the server's human-readable text, including RESP3 verbatim
strings. Wording depends on server version and current configuration; do not parse it
as a stable structured format. Prefer the typed measurements for automated monitoring.
History and histogram results allocate owned snapshots. Request specific commands and
avoid overlapping polls when inspecting large command catalogs or many nodes.

## Explicit per-node results

All six methods have `OnAllNodesAsync` counterparts returning `RespireServerResult<T>[]`.
They follow the [server fan-out contract](pub-sub-introspection.md): fresh Cluster
discovery includes replicas, execution uses temporary connections with bounded
concurrency, and each endpoint retains its own value or failure. Standalone clients
produce one endpoint result. Discovery failure or cancellation throws; after discovery,
cancellation is recorded in affected results. Inspect `IsSuccess` or `Error` before `Value`.

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

Snapshots are not atomic or aggregated across nodes. Different observation windows and
resets can make cumulative counts incomparable. Fan-out never changes Cluster slot
ownership or retries a command against another endpoint. `PurgeMemoryOnAllNodesAsync`
also requires `AllowAdmin`, returns `true` for each successful endpoint, and can partly
succeed. No rollback is possible for an allocator purge.

## Compatibility

| API pair (ordinary and `OnAllNodesAsync`) | Command | Minimum Redis version |
| --- | --- | --- |
| `LatencyDoctorAsync` | LATENCY DOCTOR | 2.8.13 |
| `LatencyHistoryAsync` | LATENCY HISTORY | 2.8.13 |
| `LatencyHistogramsAsync` | LATENCY HISTOGRAM | 7.0 |
| `MemoryDoctorAsync` | MEMORY DOCTOR | 4.0 |
| `PurgeMemoryAsync` | MEMORY PURGE | 4.0 |
| `SlowLogLengthAsync` | SLOWLOG LEN | 2.2.12 |

Valkey 7.2.5+ supports all six inherited commands, verified against its
[tagged command metadata](https://github.com/valkey-io/valkey/tree/7.2.5/src/commands).
The integration matrix covers Redis 6.2,
Redis 7.0, and Valkey 8.1 with both RESP2 and RESP3, including the older Redis error
for unsupported histograms. Unsupported commands and ACL denials remain server errors;
there is no fallback or emulation. The command minimum versions do not imply every
Respire connection or fan-out configuration works on those historical server versions.

This prerelease adds twelve required `IServerCommands` members: the six methods above
and their six `OnAllNodesAsync` counterparts. Custom implementations, decorators, and
mocks must implement or forward them.

References: Redis [LATENCY DOCTOR](https://redis.io/docs/latest/commands/latency-doctor/),
[LATENCY HISTORY](https://redis.io/docs/latest/commands/latency-history/),
[LATENCY HISTOGRAM](https://redis.io/docs/latest/commands/latency-histogram/),
[MEMORY DOCTOR](https://redis.io/docs/latest/commands/memory-doctor/),
[MEMORY PURGE](https://redis.io/docs/latest/commands/memory-purge/), and
[SLOWLOG LEN](https://redis.io/docs/latest/commands/slowlog-len/);
Valkey [histograms](https://valkey.io/commands/latency-histogram/),
[history](https://valkey.io/commands/latency-history/), and
[memory purging](https://valkey.io/commands/memory-purge/).
