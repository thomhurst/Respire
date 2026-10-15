---
title: RESP012 — Keep subscription handlers non-blocking
description: Avoid blocking waits in message consumers and delivery-gap callbacks.
---

# RESP012: keep subscription handlers non-blocking

The bundled warning detects known blocking operations inside `await foreach`
consumers of `RespireSubscription` or `IAsyncEnumerable<RespireMessage>`, including
implementations and derived interfaces, and `WithCancellation` / `ConfigureAwait`
streams. It also checks inline lambdas and method groups declared in the same
source file and registered with
`RespireSubscription.DeliveryGap`.

```csharp
async Task ConsumeAsync(
    RespireSubscription subscription, Func<RespireMessage, Task> processAsync)
{
    subscription.DeliveryGap += gap => Thread.Sleep(100); // RESP012

    await foreach (var message in subscription)
    {
        Thread.Sleep(100); // RESP012
        processAsync(message).Wait(); // RESP012
    }
}
```

Recognized operations include `Thread.Sleep` / `Join`, task `.Result`, `.Wait()`
and direct `.GetAwaiter().GetResult()` chains, `Task.WaitAll` / `WaitAny`,
`SemaphoreSlim.Wait`, `ManualResetEventSlim.Wait`, and `WaitHandle` waits.
Timed waits with a constant zero millisecond timeout or `TimeSpan.Zero` are
excluded because they only probe availability. `Thread.Sleep(0)` still yields
the consumer thread and is reported.

Blocking work in other methods, delegate variables, arbitrary synchronous I/O,
locks, and CPU-intensive work is not inferred. Nested lambdas and local functions
are excluded from message-loop traversal; work inside them may run elsewhere.

Await asynchronous work inside the message loop:

```csharp
async Task ConsumeAsync(
    RespireSubscription subscription, Func<RespireMessage, Task> processAsync,
    CancellationToken stoppingToken)
{
    await foreach (var message in subscription.WithCancellation(stoppingToken))
    {
        await processAsync(message);
    }
}
```

`DeliveryGap` runs synchronously on the receive path. Keep it short and hand off
work to a bounded worker queue; do not turn the callback into `async void`.
Long processing in a message loop can still fill its subscription buffer even
when it uses `await`. Choose buffering and overflow behavior deliberately; see
[pub/sub](../guides/pub-sub.md).

There is no automatic fix because the appropriate worker lifetime, queue policy,
and cancellation behavior belong to the application. [RESP009](../guides/async-only.md)
already offers an await fix for eligible sync-over-async Respire calls.

To change the warning's severity in `.editorconfig`:

```ini
dotnet_diagnostic.RESP012.severity = suggestion
```
