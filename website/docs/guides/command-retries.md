---
title: Command retry policy
---

Set `RespireOptions.RetryPolicy` to enable retries for buffered immediate commands
on standalone clients. The default is `null`, which retains native optimized
dispatch and performs no automatic retries.

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

Only plain `SET key value` is refined to `WriteLastWins`; option-sensitive SET,
ZADD (including INCR), and XADD retain their conservative audited categories.
Increasing retry risk can repeat effects and cannot recover the original reply.

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
command was not submitted. A `READONLY` retry requires dispatch to refresh its
primary route. Standalone dispatch selects the current multiplexer on every attempt.
Sentinel, Cluster, explicit raw/script requests, and failover-group policy integration
remain tracked by [#1215](https://github.com/thomhurst/Respire/issues/1215).

Replica read routing, streams, fire-and-forget operations, deferred batch/transaction members, connection
protocol commands, cursor continuations, and commands categorized as `Never` do not
use this retry owner. Caller-supplied raw names never acquire retry permission from
their diagnostic name. Use an audited `RespireCommands` descriptor for generated
buffered commands.

The `respire.command.retries` counter records `respire.command.retry.category` and
`respire.command.retry.outcome`. `attempted` counts accepted resends, while `succeeded`
and `exhausted` report one terminal outcome per producer that accepted a resend.
Failed circuit admission does not invent an attempt. Coalesced waiters do not duplicate
producer retry events. Handled attempt failures and final caller errors retain their
separate error owners. Exporter exceptions cannot change retry decisions or replies.

Deferred batches and transactions remain conservative: resending an
individual member can violate complete-unit ownership or atomicity. Whole-unit retry
work stays tracked by [#1216](https://github.com/thomhurst/Respire/issues/1216).
