---
title: Logical command hooks
description: Admit and observe ordinary logical commands, including native sends and single-key cache hits.
---

# Logical command hooks

Set `RespireOptions.CommandHook` to admit and observe supported logical commands. The
default is `null`: it creates no hook context or delegate and enters no extra async wrapper.
Client views created with a key prefix, read preference, or cache bypass share the hook.

:::important Scope
This first increment supports ordinary converted-response commands and non-interpolated
`ExecuteAsync`, `ExecuteLeaseAsync` and `ExecuteFireAndForgetAsync`. It is **not a
client-wide command-blocking policy**. Specialized and aggregate coverage is still being
implemented in [#1707](https://github.com/thomhurst/Respire/issues/1707) and
[#1708](https://github.com/thomhurst/Respire/issues/1708).
:::

<!-- doc-test-tail-declaration: split-before=sealed class CommandAudit -->
```csharp
var options = new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    CommandHook = new CommandAudit(),
};
await using var client = await RespireClient.ConnectAsync(options);
var value = await client.Strings.GetStringAsync("key");

sealed class CommandAudit : RespireCommandHook
{
    public override object? OnExecuting(RespireCommandContext context)
    {
        // Applies only to the supported dispatch paths described below.
        if (context.Command == "SET")
            throw new InvalidOperationException("Writes are disabled for this operation.");
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public override void OnCompleted(RespireCommandContext context, object? state,
        RespireCommandOutcome outcome, Exception? error)
    {
        var elapsed = state is long start
            ? System.Diagnostics.Stopwatch.GetElapsedTime(start)
            : TimeSpan.Zero;
        Console.WriteLine($"{context.Command}: {outcome} in {elapsed.TotalMilliseconds:F1} ms");
    }
}
```

## Supported boundaries

The hook wraps the client's ordinary string, byte and converted-response helpers. These
include single-key `GetStringAsync`, `GetBytesAsync`, typed `GetAsync` and `TryGetAsync`,
ordinary value `SetAsync`, `Strings.LengthAsync`, and other facet methods which finish
conversion in those helpers. Native sends, ready connections, replica routing, and resident
query-cache hits use the same boundary. Single-key GET admission precedes cache lookup,
so rejecting a cached read also prevents returning its value. Each caller of a coalesced
cache miss receives its own admission and completion.

Non-interpolated `ExecuteAsync`, `ExecuteLeaseAsync`, and `ExecuteFireAndForgetAsync`
are covered for both catalog descriptors and caller-supplied command names. Raw command
names are normalized and inline arguments are omitted from the context. Do not interpret
this name-only API as a key/value authorization mechanism.

The following paths do not yet have a complete hook contract: interpolated commands,
script objects and NOSCRIPT fallback, streaming uploads/downloads, cursor pages,
dedicated blocking facets, pinned/node/fan-out APIs, subscription lifecycle, multi-key
cache reads, hash partial reads, custom facet conversions, imports, and composite helpers
such as managed locks or cache-aside factories. Some invoke covered helpers internally;
that does not make their entire public operation covered. Explicit batch and transaction
members also remain outside this command hook. Use the separate
[pipeline hook](batches-and-transactions.md#observe-or-reject-explicit-executions) for
aggregate admission and completion.

## Order and outcomes

After argument construction and preflight validation, `OnExecuting` runs synchronously
before the covered operation dispatches or reads the cache. It can return any state object.
Throwing rejects the operation and produces one `Rejected` completion with that same
exception and null state. This also applies when admission throws a cancellation exception.

After admission, caller cancellation is checked. `OnCompleted` receives the same context
and admission state, and one of `Succeeded`, `Failed`, or `Canceled`. `Canceled` means the
result boundary threw `OperationCanceledException`; other failures are `Failed`. Completion
includes the helper's reply conversion and resource cleanup, and runs before the returned
operation completes. A later caller-selected conversion of a raw `RespireResult`, or an
additional conversion performed by a surrounding composite API, is outside this boundary.
Fire-and-forget completion follows its existing write/redirect semantics: success does not
promise successful execution on the server, whose ordinary error replies are discarded.

Internal retries, redirects and hedge attempts stay inside a single admitted operation.
They do not invoke additional callbacks. Another application call, including an external
retry, is a new operation. Rejection prevents that operation's command submission; it does
not undo earlier commands, initial connections, or protocol housekeeping. Authentication,
tracking setup and connection recovery are not application command hook events.

Exceptions from `OnCompleted` are ignored, preserving the original result, cancellation,
exception and pooled-result ownership. Hooks are independent of tracing and metrics.

## Concurrency and reentrancy

Callbacks may run concurrently on one hook instance. Store per-operation state in the
value returned by `OnExecuting`; synchronize shared application state. Keep callbacks
short. They run outside transport gates, but blocking them delays their caller.

A callback can initiate another command. That command receives independent callbacks;
there is no ambient suppression. Guard against unbounded recursion, and avoid synchronously
waiting for work whose progress depends on the calling operation. Retaining the immutable
context is safe. Completion errors may contain server or application data, so choose what
your audit log records.
