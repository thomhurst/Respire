# Why Respire

What Respire is for, where it shines, and the design bets behind it. The README covers the
same ground briefly; this is the longer answer.

## The one-paragraph pitch

Respire is a Redis/RESP client designed in 2026 with no legacy API to carry. It keeps the
performance architecture serious clients need — multiplexed connections, automatic
pipelining, pooled buffers, zero-allocation serialization — then adds bounded RESP3
server-assisted caching so hot reads can avoid the network entirely. A modern, hard-to-misuse
.NET surface sits on top: real return types with honest nullability, async streams for pub/sub
and streams, `TimeSpan` everywhere, typed transaction results, and built-in OpenTelemetry.

## Where Respire shines

### 1. Hot reads skip the network

Enable one option and deterministic keyed reads become cache-aware without changing call sites:

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ClientSideCache = new(),
});

string? name = await redis.GetStringAsync("user:42:name");
```

Redis tracks opted-in reads and pushes invalidations when keys change. Respire owns bounded local
storage, negative entries, multi-key dependencies, mutation fencing, reconnect flushes, and race
protection; the next read refreshes lazily. StackExchange.Redis 3.1.13 does not include an
equivalent built-in cache. Its keyspace notifications can support a custom solution, but the
application must build and operate that solution.

An official net10 BenchmarkDotNet short run measured cached Respire reads at 129.5–466.5 ns versus
185.6–186.8 μs for StackExchange.Redis server reads. This is the value of removing the round trip;
the two uncached clients measured the same statistically. See the
[benchmark run](https://github.com/thomhurst/Respire/actions/runs/31848970849).

### 2. Blocking commands are first-class

Multiplexing clients share few connections across all callers, so one `BLPOP` would stall
every command pipelined behind it — which is why StackExchange.Redis forbids blocking
commands outright. Respire keeps multiplexing for regular traffic *and* maintains a small
pool of dedicated connections that blocking calls transparently rent:

```csharp
string? job = await redis.Lists.LeftPopAsync("jobs", waitFor: TimeSpan.FromSeconds(30));
var moved  = await redis.Lists.MoveAsync("todo", "doing", waitFor: TimeSpan.FromSeconds(5));
await foreach (var entry in redis.Streams.ReadGroupAsync("events", "grp", consumer)) { … }
```

Work queues, reliable-queue patterns (`BLMOVE`), and stream consumer groups work the way the
Redis docs describe them, with no second client library and no hand-rolled polling.

### 3. The API tells the truth

- `GetStringAsync` returns `string?` — the annotation *is* the missing-key contract.
- `ExpiryAsync` returns a struct that distinguishes "no key" from "no expiry" instead of
  the raw -2/-1 sentinels.
- Batch results throw if read before the batch is sent — the await-before-flush deadlock that
  bites SE.Redis users is impossible by construction.
- `RespireTimeoutException` says the command may still execute server-side, because with
  pipelining that is the truth, and tells you what to check next.
- Cancelling a command abandons the wait, never a partially-written frame. The docs say so
  because the wire layer guarantees it.

### 4. Performance without ceremony

Pipelining is not an API you call — it's what the write path does. Concurrent commands from
every caller coalesce into one buffer and leave in a single syscall; the flush loop is a
persistent task, not a per-flush allocation; replies are parsed out of pooled buffers with
exactly one copy off the socket; completion sources and async state machines are pooled.

When you *do* want zero-copy or explicit flushing, both are spelled out:

```csharp
using var lease = await redis.Strings.GetLeaseAsync("blob");   // pooled, no copy
var batch = redis.CreateBatch(); … await batch.ExecuteAsync();    // one explicit flush
```

`benchmarks/Respire.ComparisonBenchmarks` tracks throughput and allocations against
StackExchange.Redis.

### 5. Modern .NET is the interface

`IAsyncEnumerable` pub/sub and stream reading, `params ReadOnlySpan<>` variadics without
allocation, interpolated-string raw commands where each hole is exactly one argument,
`record` options with init-only setters, keyed DI registrations, and nullability annotations
throughout. None of this is decoration — each one removes a class of bugs or boilerplate the
older API styles force on you.

### 6. Operations people are not an afterthought

- `ActivitySource("Respire")` + `Meter("Respire")` follow OTel database semantic conventions;
  enabling them is one line each and they cost nothing when off.
- Dead connections are replaced in the background; pub/sub reconnects **and resubscribes**
  itself; `ConnectionStateChanged` reports transitions.
- `ClientName` in options → `CLIENT SETNAME` in the handshake, so `CLIENT LIST` on a busy
  server tells you which service owns which connection.
- DI registration is lazy: your app starts even when Redis is down, and the first command
  connects.

## Design bets (and what we rejected)

| Bet | Rejected alternative | Why |
|---|---|---|
| Server-assisted client cache | Every read crosses the network, or every app builds its own cache | Redis tracking gives coherent hot reads without application invalidation plumbing |
| Facets per data type (`redis.Hashes.GetAsync`) | 400 flat prefixed methods | IntelliSense as documentation; the facet is the namespace |
| Real return types, serializer for `T` | Protocol union struct (`RedisValue`-style) | Union types push protocol details and disposal onto every caller |
| Explicit lease API for zero-copy | Disposable results everywhere | A disposal obligation should be visible at the call site, not ambient |
| Blocking commands on dedicated pooled connections | Forbidding them | The capability is why people use lists/streams as queues |
| Throwing server errors | Error-as-value inspection | One error model; `.Code` carries the Redis error class |
| `Async` suffix kept | Dropping it | Analyzer ecosystem and reader expectations beat the keystrokes |

The full surface, conventions, and roadmap live in
[API_DESIGN.md](API_DESIGN.md).

## When *not* to use Respire (yet)

Honesty section. Today Respire still lacks automatic Sentinel failover, cluster-mode `WATCH`
transactions, and cluster sharded pub/sub. StackExchange.Redis also has a much longer production
history and ecosystem. If those capabilities or maturity outweigh Respire's server-assisted
cache, blocking-command pool, and modern API, StackExchange.Redis remains the safer choice.
