# Async-only APIs

Respire's native network APIs are async-only. Use `await` for commands, connection
setup, batch execution, and transaction commits. Respire does not provide a second
set of synchronous command methods or a native blocking context.

```csharp
var value = await client.GetStringAsync("key");
await client.SetAsync("key", "value");
```

This keeps one execution model for multiplexed connections, cancellation, and
retries. Blocking a calling thread with `.Result`, `.Wait()`, or
`.GetAwaiter().GetResult()` does not turn asynchronous transport into synchronous
I/O. It can starve the thread pool and can deadlock when continuations need a
blocked synchronization context. `ConfigureAwait(false)` on a blocking caller
does not make that pattern safe.

Make the calling path asynchronous too: return `Task`, `Task<T>`, `ValueTask`, or
`ValueTask<T>` as appropriate and await it upstream. Use asynchronous factory
methods instead of doing network I/O in constructors. Move I/O outside `lock`
bodies; use an asynchronous coordination primitive when mutual exclusion is needed.
Wrapping a blocking call in `Task.Run` still consumes a thread while waiting.

Synchronous in-memory operations, such as building options, creating a batch,
reading a completed queued result, and disposing resources, are separate from
network command execution. [Server-blocking commands](blocking-queues.md) also
remain asynchronous for the caller. The optional
[StackExchange.Redis compatibility adapter](stackexchange-interop.md) has its own
documented synchronous compatibility surface; it does not add native sync APIs.

## RESP009: await Respire operations

The `Respire` NuGet package includes this warning and its **Await the Respire
operation** code fix under `analyzers/dotnet/cs`. There is no extra analyzer package
to install. Keep analyzer assets enabled on the package reference.

```csharp
// RESP009
var value = client.GetStringAsync("key").Result;
client.SetAsync("key", "value").AsTask().Wait();
var other = client.GetStringAsync("other").ConfigureAwait(false).GetAwaiter().GetResult();

// After applying the fix inside an async method
var value = await client.GetStringAsync("key");
await client.SetAsync("key", "value").AsTask();
var other = await client.GetStringAsync("other").ConfigureAwait(false);
```

The rule recognizes `Task` and `ValueTask` results from methods in the `Respire`
namespace and its children, including extension methods, and user interfaces
marked `[RespireCommands]` and their implementing methods. It follows `AsTask()`, `ConfigureAwait(...)`,
`WaitAsync(...)`, and local initializer aliases that are never reassigned or
passed by reference. It does not trace tasks through fields, parameters, arbitrary
helper methods, or reassignment. This is a targeted diagnostic, not a proof that
all calling code is asynchronous.

The fix preserves `ConfigureAwait` and `WaitAsync` arguments. It can add `async`
to an existing task-returning method or local function when the edited document
compiles without changing its signature. Otherwise, make the containing method
asynchronous and update callers manually. No fix is offered for constructors,
properties, `lock` bodies, or timed/cancellable `Wait(...)` overloads: replacing
those calls directly could change their contract. An unrelated compile error in
the document can also prevent the fix from being offered. Fix All is available
for supported sites.

`RespirePending<T>.Result` is a completed queued-result read, not a task-blocking
API. [RESP002](batches-and-transactions.md#ignoring-queued-results) checks that its
batch or transaction has been flushed first; RESP009 does not duplicate that rule.

Configure the warning through `.editorconfig` if your project needs a different severity:

```ini
[*.cs]
dotnet_diagnostic.RESP009.severity = warning
```
