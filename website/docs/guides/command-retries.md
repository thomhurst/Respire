---
title: Command retry policy
---

`RespireRetryPolicy` defines the safety and delay settings for immediate-command
retry dispatch. This release supplies the policy foundation only. There is no
`RespireOptions` switch or client view that activates automatic command retries
yet. Dispatch integration is tracked by [#1215](https://github.com/thomhurst/Respire/issues/1215)
and its native sub-issues. Existing commands keep their current dispatch behavior.

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
primary route. Future failover-group retries must select the group's current active
member for each attempt. These route, ownership, and telemetry integrations are
remaining work under #1215; this policy alone performs no sends and emits no retry
events.

The immediate-command integration must retain arguments, payloads, replies, cache
mutation barriers, coalesced producer identity, and final publication ownership across
attempts. Deferred batches and transactions must remain conservative: resending an
individual member can violate complete-unit ownership or atomicity. Whole-unit retry
work stays tracked by [#1216](https://github.com/thomhurst/Respire/issues/1216).
