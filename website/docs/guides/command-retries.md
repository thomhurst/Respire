---
title: Command retry policy
---

Set `RespireOptions.RetryPolicy` to enable retries for buffered immediate commands
on standalone, Sentinel and Cluster clients. The default is `null`, which retains
native optimized dispatch and performs no automatic retries.

```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new("localhost", 6379) },
    RetryPolicy = new RespireRetryPolicy
    {
        MaxAttempts = 3,
        MaxCommandRetryCategory = RespireCommandRetryCategory.ReadOnly,
    },
});
```

Public typed reads, conversions, and generated catalog commands share one retry
owner. Arguments are copied before asynchronous dispatch, so later caller memory
changes cannot change a resend. Each attempt consumes immutable transport write
evidence. One deadline and cancellation budget span connection selection, all
sends, and backoff. Cache mutation fences and coalesced cache producers retain
their logical ownership across all attempts and final conversion. Cancelled native
responses retain their fences until their FIFO reply slots retire.

Typed SET and ZADD refine their category only for audited option sets:

- `SET key value` and `SET key value XX` are `WriteLastWins`.
- `SET key value NX` is `WriteChecked`, the same as `SETNX`.
- `ZADD key XX` is `WriteLastWins`, and `ZADD key NX` is `WriteChecked`.

SET with an expiry, `KEEPTTL` or `GET`, ZADD with `INCR`, `CH`, `GT` or `LT`, and
XADD keep their conservative audited categories. A checked write cannot repeat
its effect, but a replay can report a different result, such as "not set" for a
write that the first attempt applied. An overwrite can replace an intervening
update. Increasing retry risk can repeat effects and cannot recover the original
reply.

## Topologies

Standalone and Sentinel clients share the owner that consumes transport write
evidence. Each Sentinel attempt resolves the current primary generation. A
`READONLY` reply retires that generation before the retry, so the retry discovers
and writes to the promoted primary. A lost primary connection also retires the
generation and triggers discovery.

Cluster attempts keep `MOVED`, `ASK`, retirement and `READONLY` recovery inside the
existing Cluster routing owner. A retry starts only after an attempt's final failure.
That routing does not expose transport write evidence, so every failed Cluster attempt
counts as possibly executed. Only the category permission can admit another attempt;
a definitely unwritten frame does not unlock categories above the policy maximum.
The logical deadline bounds every redirect inside every attempt. Redirects never
count as retry attempts.

## Caller-declared categories

Respire never infers retry permission from a caller-supplied command name or from
read routing. Declare the risk of the complete invocation explicitly:

```csharp
var read = RespireCommand.Create("MYMODULE.GET")
    .WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
using var result = await redis.ExecuteAsync(read, ["key"]);

var script = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true)
    .WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
var value = await redis.Scripts.ExecuteStringAsync(script, ["key"]);

var function = RespireFunction.Create("reader", readOnly: true)
    .WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
```

The declaration replaces any audited category on that descriptor, and Respire does
not validate it against the name or arguments. Scripts and functions default to
`Never`, including read-only ones. A declared script or function retries its complete
exchange, including the `EVALSHA`/`EVAL` fallback or a library reload. Its arguments
are copied before the first send. Every failed script or function attempt counts as
possibly executed, because it may have run before its reply was lost.

Raw `EVAL` and `FCALL` sent through `ExecuteAsync`, blocking commands, interpolated
commands, and fire-and-forget sends do not retry. Use `RespireScript` or
`RespireFunction` with a declared category instead.

## Failover groups

Set `RespireFailoverGroupOptions.RetryPolicy` and run operations through
`RespireFailoverGroup.ExecuteAsync`:

```csharp
RespireFailoverCandidate[] candidates =
[
    new(new RespireOptions { Endpoints = { new("primary.example", 6379) } }, Priority: 0),
    new(new RespireOptions { Endpoints = { new("standby.example", 6379) } }, Priority: 1),
];
await using var group = await RespireFailoverGroup.ConnectAsync(candidates, new RespireFailoverGroupOptions
{
    RetryPolicy = new RespireRetryPolicy { MaxAttempts = 3 },
});
var value = await group.ExecuteAsync(
    (client, token) => client.GetStringAsync("key", token),
    RespireCommandRetryCategory.ReadOnly);
```

Each attempt reads `ActiveClient` again, so a retry after a switch uses the newly
active member. When no member is healthy, the attempt fails as a connection failure
and can retry within the backoff budget. The group cannot inspect the operation, so
its category is required and every failed attempt counts as possibly executed.
Members keep their own `RetryPolicy`; a group retry starts only after a member's
operation fails. The group has no command timeout of its own. Each attempt uses its
member's timeout, and the caller's token bounds the whole operation.

## Policy settings

| Setting | Default | Bounds and meaning |
| --- | --- | --- |
| `MaxAttempts` | `3` | From 1 through 100, including the initial send. 1 allows no retry. |
| `MaxCommandRetryCategory` | `ReadOnly` | Maximum accepted risk after possible execution. `Never` disables retries. |
| `RetryableErrors` | `ConnectionLoss`, `Loading`, `TryAgain`, `MasterDown`, `ReadOnly` | Only these named flags are supported. `None` selects no failures. |
| `InitialBackoff` | 10 milliseconds | First retry's delay ceiling. Zero disables delay. |
| `MaxBackoff` | 1 second | At least `InitialBackoff`, at most 1 minute. |
| `Jitter` | `1` | Finite fraction from 0 through 1. 0 uses the ceiling; 1 randomizes from zero to the ceiling. |

The delay ceiling doubles after each completed attempt, capped at `MaxBackoff`.
With partial jitter, the delay ranges from `ceiling * (1 - Jitter)` to the ceiling.
Each retry must fit within the original logical deadline. The policy never starts
a new timeout budget. Cancellation stops retry admission. Dispatch must recheck
deadline and cancellation after backoff, immediately before another send.

## Execution risk

The audited categories form these explicit permission sets after possible execution:
`Always`; then `Connection`; then `ReadOnly`; then `WriteChecked`; then
`WriteLastWins`; then `WriteAccumulating`; then `ServerAdmin`. Selecting a category
permits it and the preceding named categories. Numeric enum comparisons are not
the contract. `Never` and invalid categories never authorize a retry.

A known command whose frame is **definitely unwritten** can be retried under any
enabled category selection. Both **unknown** write evidence and **possible
execution** require the category permission above. An exception's
`IsCommandNotSubmitted` flag, message, or nested cause does not upgrade transport
evidence. Unknown commands and scripts need an explicit caller category; recognizing
a command name does not grant permission.

Even a repeated read can return different data. A checked write can return a different
result after its first effect. An overwrite can destroy an intervening update.
Selecting `WriteAccumulating` or `ServerAdmin` explicitly accepts duplicate effects,
consumed data, and lost original results. Retrying never provides exactly-once
execution or recovers the original reply.

## Failures and dispatch scope

Eligibility requires both a selected failure and safe execution risk. Server replies
`LOADING`, `TRYAGAIN`, `MASTERDOWN`, and `READONLY` are individually selectable.
Authentication failures, exhausted connection recovery, protocol/configuration
errors, cancellation, and circuit admission rejection are ineligible. Arbitrary
server errors, including `BUSY` and `CLUSTERDOWN`, are not selected by this foundation.
`MOVED` and `ASK` remain owned by existing Cluster routing.

A circuit rejection never authorizes a resend to that endpoint, even though the
command was not submitted. A retry that waits for a replacement connection first
rejects an open circuit again. A `READONLY` retry requires dispatch to refresh its
primary route. Standalone dispatch selects the current multiplexer on every attempt,
and Sentinel dispatch selects the current generation. The full circuit and retry
composition, including half-open contention and failover switches, remains tracked by
[#1256](https://github.com/thomhurst/Respire/issues/1256).

Replica read routing, streams, fire-and-forget operations, deferred batch/transaction members, connection
protocol commands, cursor continuations, client-cache tracked reads on Cluster, and
commands categorized as `Never` do not use a retry owner. Caller-supplied raw names
never acquire retry permission from their diagnostic name. Use an audited
`RespireCommands` descriptor for generated buffered commands, or declare a category.

The `respire.command.retries` counter records `respire.command.retry.category` and
`respire.command.retry.outcome`. On standalone and Sentinel clients, `attempted` counts
accepted resends. Cluster, script, function and failover-group owners have no transport
acceptance signal, so `attempted` counts started resends. `succeeded` and `exhausted`
report one terminal outcome per producer that made a resend.
Failed circuit admission does not invent an attempt. Coalesced waiters do not duplicate
producer retry events. Handled attempt failures and final caller errors retain their
separate error owners. Exporter exceptions cannot change retry decisions or replies.

Deferred batches and transactions remain conservative: resending an
individual member can violate complete-unit ownership or atomicity. Whole-unit retry
work stays tracked by [#1216](https://github.com/thomhurst/Respire/issues/1216).
