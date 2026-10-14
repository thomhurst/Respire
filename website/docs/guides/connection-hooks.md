---
title: Physical connection hooks
description: Audit, time, or reject physical connection establishment without exposing credentials or command data.
---

# Physical connection hooks

Set `RespireOptions.ConnectionHook` to observe or reject each physical connection attempt.
The default is `null`; that path creates no hook context, delegate, or extra async wrapper.

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

`OnConnectingAsync` runs before credential acquisition, DNS, or socket creation. Throwing
rejects the attempt. `OnCompletedAsync` receives `Succeeded`, `Failed`, or `Canceled`, and
elapsed time measured from before the first callback. A failed attempt includes a rejection
from the first callback and a connection timeout. `Canceled` means initiating cancellation;
an independent connect timeout is `Failed`.

The same context instance identifies both callbacks for an attempt. Its only properties are
the requested endpoint and effective TLS setting. No credentials, command values, server
replies, exception text, or connection handle are passed to the hook. The endpoint is the
requested name before DNS resolution, or a Unix socket path with port zero. A hostname check
like the example is an application policy, not a check of the resolved network address.

## Completion boundary

Success includes transport connection, TLS, authentication, RESP negotiation, database
selection, client naming, tracking, `READONLY`, and other configured connection-local setup.
Generation validation performed inside connection establishment is also included. Routing
and topology work performed by callers, such as Cluster discovery and replica `ROLE` checks,
can still fail after the hook reports success. A success callback does not promise that the
connection will remain open or that the whole client is ready.

The hook applies to initial connections, reconnects, dedicated connections, subscriptions,
standalone replicas, Cluster nodes, Sentinel discovery, and Sentinel monitoring. Sentinel
attempts report the effective Sentinel TLS override. Renting an already-open dedicated
connection and refreshing credentials on an existing connection do not establish a new
physical connection and do not invoke the hook.

## Errors, cancellation, and concurrency

The completion callback runs once, including when the first callback rejects or fails.
If the initiating token was already canceled, the first callback is skipped and completion
reports `Canceled`. Failure cleanup completes before the completion callback runs. If the
completion callback throws after success, the connection is rejected and disposed before
the caller sees that error. Completion is not called a second time. If establishment already
failed, a completion callback exception is ignored to preserve the original failure.

Both callbacks receive the attempt's initiating cancellation token, which can be linked to
client or pool lifetime. Hooks must cooperate with cancellation. Cancellation is checked after
each callback so ignoring cancellation cannot publish a newly canceled connection. Transport
and handshake deadlines retain their existing scope; `ConnectTimeout` does not bound callback
execution. Use a caller deadline or a bounded callback operation when needed. For audit writes
that must survive cancellation, use a separate bounded token inside the completion callback.

One hook may be called concurrently by several connection attempts. Keep shared state thread
safe and correlate attempts by context identity. Do not await operations on the client being
connected: those operations may require the very connection that is waiting for the hook.
Hook failures follow the existing initial-connect and reconnect policies; rejecting a
reconnect can consume its attempt budget. Hooks do not wrap commands or select tracing.
