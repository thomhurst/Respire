---
title: Redis Functions
description: Call named Redis functions and manage their server-side libraries.
---

# Redis Functions

`redis.Functions` in the core `Respire` package calls named functions on Redis 7+.
Unlike an application-owned [Lua script](../guides/scripting.md), a function belongs to
a named library deployed on the server.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var library = RespireFunctionLibrary.Create("""
    #!lua name=readers
    redis.register_function{function_name='read_value', callback=function(keys,args)
        return redis.call('GET', keys[1])
    end, flags={'no-writes'}}
    """);
var read = library.Function("read_value", readOnly: true);
string? value = await redis.Functions.ExecuteStringAsync(read, keys: ["message"]);
```

An immediate call using a library object can load a missing library and retry once.
For separately deployed libraries, use `RespireFunction.Create("read_value", readOnly: true)`.
`readOnly: true` sends `FCALL_RO` and follows `ReadFrom`; the function must permit read-only
execution. Other calls send `FCALL`.

## Results and retries

- `ExecuteAsync` returns a GC-owned `RespireResult`; disposal is optional.
- `ExecuteLeaseAsync` and `ExecuteLeaseSpanAsync` return pooled results: dispose them with `using`.
- `ExecuteStringAsync`, `ExecuteIntegerAsync`, and `ExecuteAsync<T>` convert and dispose replies.
- Keys are prefixed; arguments and function/library names are not. All keys must share a
  Cluster slot. Keyless calls use the router's default node.
- Functions default to `RespireCommandRetryCategory.Never`. Opt in with `WithRetryCategory`
  only when the function's repeated effects are safe. A read-only flag alone does not opt in.

See [automatic library loading](../guides/scripting.md#automatic-library-loading) for
source collision handling, replica recovery, ACL requirements, and deployment races.

## Library administration

| Method | Redis command | Cluster scope |
| --- | --- | --- |
| `LoadAsync` | FUNCTION LOAD | All discovered primaries |
| `DeleteAsync` | FUNCTION DELETE | All discovered primaries |
| `FlushAsync` | FUNCTION FLUSH | All discovered primaries |
| `RestoreAsync` | FUNCTION RESTORE | All discovered primaries |
| `ListAsync` | FUNCTION LIST | One routing node |
| `DumpAsync` | FUNCTION DUMP | One routing node |
| `StatsAsync` | FUNCTION STATS | One routing node |

Libraries are server-wide: database selection and key prefixes do not isolate them.
Fan-out changes are not atomic and can leave nodes with different libraries after a failure.
See [administration options](../guides/scripting.md#function-administration) before replacing,
flushing, or restoring libraries.

`batch.Functions` and `transaction.Functions` queue calls and administration without the
`Async` suffix. Load libraries first: deferred calls never automatically reload a missing
library. Administration affects only the execution node. See
[deferred functions](../guides/scripting.md#functions-in-batches-and-transactions) for retry
and result lifetime rules.
