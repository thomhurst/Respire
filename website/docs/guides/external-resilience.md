---
title: Polly and Microsoft.Extensions.Resilience
description: Choose one retry owner and compose Respire with a Polly v8 resilience pipeline.
---

# Polly and Microsoft.Extensions.Resilience

Choose one owner for command retries. An outer pipeline with two retries and an
inner Respire policy with three attempts can send a command nine times. Retry
counts multiply; timeouts and circuit breakers also observe different scopes.

| Retry owner | Respire configuration | Outer pipeline |
| --- | --- | --- |
| Respire | Set `RetryPolicy` | Do not add retry or hedging strategies around the same operation |
| Polly v8 | Set `RetryPolicy = null`, or use `Respire.Resilience` | Configure explicit failure predicates and replay-safe callbacks |

Prefer [Respire's command policy](./command-retries) when you need its command
categories, transport write evidence, primary refresh, or whole-unit retry rules.
Keep an outer timeout or circuit breaker if needed, with its scope chosen explicitly.

## Optional adapter package

```shell
dotnet add package Respire.Resilience
```

`RespireResilienceClient` owns a lazy Respire client and borrows a Polly v8
`ResiliencePipeline`. It copies your options with `RetryPolicy = null`, even when
the supplied options enable retries. Other settings, including endpoint circuit
breakers, are preserved. Disposing the adapter disposes its client, not the pipeline.
Reuse the adapter and pipeline across operations; constructing them per request
would discard connection pools and circuit history.

```csharp
using Polly;
using Respire;
using Respire.Resilience;

var pipeline = new ResiliencePipelineBuilder()
    .AddTimeout(TimeSpan.FromSeconds(10)) // Total budget, outside retries.
    .AddRetry(new()
    {
        MaxRetryAttempts = 2, // Three attempts in total.
        Delay = TimeSpan.FromMilliseconds(100),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = new PredicateBuilder().Handle<RespireServerException>(
            error => error.Code is "LOADING" or "TRYAGAIN" or "MASTERDOWN"),
    })
    .Build();

await using var redis = new RespireResilienceClient(new RespireOptions
{
    Endpoints = { new("localhost", 6379) },
    CommandTimeout = TimeSpan.FromSeconds(2),
}, pipeline);

var value = await redis.ExecuteAsync(
    (client, token) => client.GetStringAsync("key", token));
```

This deliberately narrow predicate retries selected transient server replies for a
read. Cancellation, authentication errors, protocol errors, timeouts, and open
circuits propagate. If you add connection-loss handling, distinguish authentication
and exhausted reconnect errors from ordinary connection failures, including errors
wrapped during topology discovery. Do not use `Handle<Exception>()` or Polly's
default catch-all predicate for Redis operations.

The adapter executes the **entire callback** on each attempt. It does not inspect
commands, infer idempotency, use Respire's retry categories, or gain its internal
transport write evidence. In particular, `READONLY` in an outer predicate does not
perform the primary refresh that Respire's own retry policy performs. Topology
redirects and connection recovery still belong to Respire; disabling the command
policy does not disable protocol recovery or guarantee one network exchange per callback.

## Microsoft DI and telemetry

The [runnable sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Resilience)
uses `Microsoft.Extensions.Resilience`, `AddResiliencePipeline`,
`ResiliencePipelineProvider<string>`, and `AddResilienceEnricher`. Its fake server
rejects two GETs and then succeeds. It asserts three callback attempts even when
the input options contain a five-attempt Respire policy.

```shell
dotnet run --project samples/Respire.Samples.Resilience -f net10.0
```

Polly owns the retry and pipeline-breaker events (`OnRetry`, `OnCircuitOpened`,
`OnCircuitHalfOpened`, `OnCircuitClosed`). Configure Polly logging/metrics and
Microsoft's enrichers on the shared pipeline registration. See
[Polly telemetry](https://www.pollydocs.org/advanced/telemetry.html) and
[Microsoft.Extensions.Resilience](https://www.nuget.org/packages/Microsoft.Extensions.Resilience).
The adapter depends only on `Polly.Core`; Microsoft DI and telemetry enrichment are optional.

## Circuit breaker ownership

Respire's [endpoint breaker](./circuit-breakers) guards a specific data endpoint.
A Polly breaker guards callbacks sharing a pipeline. With a cluster or failover,
an open pipeline circuit can reject work for healthy endpoints too.

- To make Polly own breaker state and telemetry, leave `RespireOptions.CircuitBreaker`
  null and add a Polly breaker. `CircuitBreakerStateProvider` exposes its state, as
  shown in the sample. This is operation-level protection, not an endpoint registry.
- To retain endpoint admission, configure Respire's breaker. An open endpoint throws
  `RespireCircuitOpenException` with `Endpoint` and `RetryAfter`. Exclude that
  exception from retry predicates; `RetryAfter` is a snapshot, not permission to resend.
- If you use both, decide whether the pipeline breaker measures each attempt (inside
  retry) or the final logical outcome (outside retry). Do not count an endpoint's
  circuit rejection as fresh server failure evidence.

Respire endpoint state is not synchronized into Polly's breaker. Observe
`respire.circuit.transitions` and `respire.circuit.rejections` on the Respire meter
with `RespireMetricGroups.Resiliency` enabled. When Respire owns retries, that same
metric group exposes `respire.command.retries`. When Polly owns them, its `OnRetry`
events are the retry source. Export both meters when using both kinds of breaker.

## Budgets and replay safety

`RespireOptions.CommandTimeout` applies to each command attempt; it restarts on
each outer callback. Put Polly's total timeout **outside** retry, or supply a caller
token with a total deadline. Pass the callback's token to every command so timeout
and caller cancellation can stop subsequent attempts. Cancellation cannot undo a
command already executed by Redis.

Use read-only or explicitly replay-safe callbacks. Repeating `INCR`, queue pops,
stream appends, or a multi-command workflow can duplicate effects after a lost reply.
Even an overwrite can erase an intervening update. A retry never recovers a lost
result or provides exactly-once execution.

Do not nest failover-group retries, watched-transaction retry helpers, or your own
retry loops inside a retrying pipeline. Respire's WATCH-conflict callback retries
are separate from `RetryPolicy`. If replaying a batch or transaction is safe, create
and dispose a fresh complete unit inside each callback. Do not retry stream
enumeration or server-local cursor continuations as ordinary callbacks. Do not
retain or dispose the client passed to the callback, and do not wrap the adapter
in a second retrying pipeline.
