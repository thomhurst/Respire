---
title: RESP010 — Share a long-lived client
description: Avoid creating Redis connections for each request or operation.
---

# RESP010: share a long-lived client

`RespireClient` multiplexes connections and is safe to share across concurrent
operations. Creating and disposing one for each request repeats connection setup
and prevents reuse of its connection pools and caches.

```csharp
async Task<string?> ReadAsync()
{
    // RESP010
    await using var client = await RespireClient.ConnectAsync("localhost");
    return await client.GetStringAsync("key");
}
```

Create the client during application startup, or register it with
[dependency injection](../integrations/dependency-injection.md), then inject
`IRespireClient` into request handlers. The application owns its disposal.

```csharp
Task<string?> ReadAsync(IRespireClient client)
    => client.GetStringAsync("key").AsTask();
```

The bundled warning recognizes `Create`, `ConnectAsync`, and `ConnectAnyAsync`
inside loops, and in `using` / `await using` declarations or statements inside
methods. It leaves field initializers, factories returning a client, and the
application entry point's outer lifetime alone. Loop checks stop at nested
function boundaries. It does not infer DI service scopes, trace custom factories,
or prove how frequently a method runs.

For a deliberate isolated client (for example, a connectivity probe), suppress
the warning locally with `#pragma warning disable RESP010` and restore it after
the creation. An application-lifetime owner method other than `Main` may also
need this suppression. No automatic code fix is offered: moving ownership into
application startup requires choosing its configuration and disposal lifetime.

To change its severity in `.editorconfig`:

```ini
dotnet_diagnostic.RESP010.severity = suggestion
```
