---
title: Physical connection hooks
description: Log, time, or reject physical connection establishment without exposing credentials or command data.
---

# Physical connection hooks

Set `RespireOptions.ConnectionHook` to log, time or reject each physical connection attempt.
The default is `null`, which adds no overhead.

<!-- doc-test-tail-declaration: split-before=sealed class ConnectionAudit -->
```csharp
var options = new RespireOptions
{
    Endpoints = [new("redis.internal", 6379)],
    ConnectionHook = new ConnectionAudit(),
};
await using var client = await RespireClient.ConnectAsync(options);

sealed class ConnectionAudit : RespireConnectionHook
{
    public override ValueTask OnConnectingAsync(
        RespireConnectionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Endpoint.Host.EndsWith(".internal", StringComparison.Ordinal))
            throw new InvalidOperationException("Endpoint rejected by application policy.");
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnCompletedAsync(
        RespireConnectionContext context, RespireConnectionOutcome outcome,
        TimeSpan elapsed, CancellationToken cancellationToken)
    {
        Console.WriteLine($"{context.Endpoint}: {outcome} after {elapsed.TotalMilliseconds:F1} ms");
        return ValueTask.CompletedTask;
    }
}
```

## Callbacks

- `OnConnectingAsync` runs before credential acquisition, DNS or socket creation. Throw to reject the attempt.
- `OnCompletedAsync` runs exactly once per attempt with the outcome (`Succeeded`, `Failed` or `Canceled`) and the elapsed time since before the first callback. A rejection from `OnConnectingAsync` or a connect timeout reports `Failed`; `Canceled` means the initiating token was cancelled.

The same `RespireConnectionContext` instance is passed to both callbacks for an attempt. It
exposes only the requested endpoint and the effective TLS setting. The hook never sees
credentials, command values, server replies, exception text or the connection itself. The
endpoint is the requested name before DNS resolution (or a Unix socket path with port zero),
so a hostname check like the example does not check the resolved address.

## What counts as success

`Succeeded` means the transport connected and all connection-local setup finished: TLS,
authentication, RESP negotiation, database selection, client naming, tracking, `READONLY`
and similar. Work done afterwards by the caller, such as Cluster discovery or replica `ROLE`
checks, can still fail. Success does not mean the connection will stay open or that the whole
client is ready.

The hook runs for initial connections, reconnects, dedicated connections, subscriptions,
standalone replicas, Cluster nodes, Sentinel discovery and Sentinel monitoring. Sentinel
attempts report the effective Sentinel TLS setting. Renting an already-open dedicated
connection, or refreshing credentials on an existing connection, does not invoke the hook.

## Errors and cancellation

- If the initiating token is already cancelled, `OnConnectingAsync` is skipped and completion reports `Canceled`.
- Failure cleanup finishes before `OnCompletedAsync` runs.
- If `OnCompletedAsync` throws after a success, the connection is disposed and the caller sees that exception. If the attempt had already failed, the callback's exception is ignored and the original failure is kept.
- Both callbacks receive the attempt's cancellation token, which may be tied to the client's lifetime. Respire checks it after each callback, so a cancelled attempt never yields a connection.
- `ConnectTimeout` does not bound hook execution. Bound slow work yourself. For log writes that must survive cancellation, use a separate bounded token inside `OnCompletedAsync`.
- Hook failures follow the initial-connect and reconnect policies; rejecting a reconnect can use up its attempt budget.

## Concurrency

One hook instance can be called concurrently by several connection attempts. Keep shared state
thread-safe and correlate callbacks by context identity. Do not call the client being
connected from inside the hook: the operation may need the connection that is waiting on the
hook. Connection hooks do not see commands; use [command hooks](./command-hooks) for that.
