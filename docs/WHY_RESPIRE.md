# Why Respire

What Respire is for, where it shines, and the design bets behind it. The README covers the
same ground briefly; this is the longer answer.

## The one-paragraph pitch

Respire is a Redis/RESP client designed in 2026 with no legacy API to carry. It keeps the
performance architecture serious clients need — multiplexed connections, automatic
pipelining, pooled buffers, zero-allocation serialization — and puts a modern, hard-to-misuse
.NET surface on top: real return types with honest nullability, async streams for pub/sub and
streams, `TimeSpan` everywhere, typed transaction results, and built-in OpenTelemetry.

## Where Respire shines

### 1. Blocking commands are first-class

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

### 2. The API tells the truth

- `GetStringAsync` returns `string?` — the annotation *is* the missing-key contract.
- `ExpiryAsync` returns a struct that distinguishes "no key" from "no expiry" instead of
  the raw -2/-1 sentinels.
- Batch results throw if read before the batch is sent — the await-before-flush deadlock that
  bites SE.Redis users is impossible by construction.
- `RespireTimeoutException` says the command may still execute server-side, because with
  pipelining that is the truth, and tells you what to check next.
- Cancelling a command abandons the wait, never a partially-written frame. The docs say so
  because the wire layer guarantees it.

### 3. Performance without ceremony

Pipelining is not an API you call — it's what the write path does. Concurrent commands from
every caller coalesce into one buffer and leave in a single syscall; the flush loop is a
persistent task, not a per-flush allocation; replies are parsed out of pooled buffers with
exactly one copy off the socket; completion sources and async state machines are pooled.

When you *do* want zero-copy or explicit flushing, both are spelled out:

```csharp
using var lease = await redis.Strings.GetLeaseAsync("blob");   // pooled, no copy
var batch = redis.CreateBatch(); … await batch.SendAsync();    // one explicit flush
```

`benchmarks/Respire.ComparisonBenchmarks` tracks throughput and allocations against
StackExchange.Redis.

### 4. Modern .NET is the interface

`IAsyncEnumerable` pub/sub and stream reading, `params ReadOnlySpan<>` variadics without
allocation, interpolated-string raw commands where each hole is exactly one argument,
`record` options with init-only setters, keyed DI registrations, and nullability annotations
throughout. None of this is decoration — each one removes a class of bugs or boilerplate the
older API styles force on you.

### 5. Operations people are not an afterthought

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
| Facets per data type (`redis.Hashes.GetAsync`) | 400 flat prefixed methods | IntelliSense as documentation; the facet is the namespace |
| Real return types, serializer for `T` | Protocol union struct (`RedisValue`-style) | Union types push protocol details and disposal onto every caller |
| Explicit lease API for zero-copy | Disposable results everywhere | A disposal obligation should be visible at the call site, not ambient |
| Blocking commands on dedicated pooled connections | Forbidding them | The capability is why people use lists/streams as queues |
| Throwing server errors | Error-as-value inspection | One error model; `.Code` carries the Redis error class |
| `Async` suffix kept | Dropping it | Analyzer ecosystem and reader expectations beat the keystrokes |

The full surface, conventions, and the roadmap (cluster/sentinel, RESP3-first
internals, client-side caching, source-generated module commands) live in
[API_DESIGN.md](API_DESIGN.md).

## When *not* to use Respire (yet)

Honesty section. Today Respire does not do:

- **Cluster / Sentinel** — single endpoint only for now.
- **Client-side caching** (RESP3 tracking) — designed for, not shipped.

If you need those today, StackExchange.Redis remains the mature choice. If you don't, Respire
gives you a cleaner API on a faster wire — and those gaps are the roadmap's top items.
