---
title: Logical command hooks
description: Admit and observe logical commands, including scripts, cursor pages, blocking replies, streaming results and subscriptions.
---

# Logical command hooks

Set `RespireOptions.CommandHook` to admit and observe supported logical commands. The
default is `null`: it creates no hook context or delegate and enters no extra async wrapper.
Client views created with a key prefix, read preference, or cache bypass share the hook.

:::important Scope
Supported routes include ordinary converted-response commands, interpolated and non-interpolated Execute
APIs, function calls and mutations, script execution, dedicated blocking converted replies, ordinary cursor pages,
streaming results, subscription setup/teardown, specialized server operations and cluster scan pages.
This is **not a client-wide command-blocking policy**. Remaining specialized dispatch and
aggregate coverage are tracked in [#1708](https://github.com/thomhurst/Respire/issues/1708).
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

The interpolated overloads of all three Execute APIs also admit after preflight validation
and before dispatch. With a hook configured, tokens and caller-owned binary arguments are
snapshotted before admission; dispatch uses that same snapshot even if the hook changes the
original buffers or appends to an explicitly constructed handler.
Literal command names and recognized subcommands are normalized;
interpolated arguments are never included in the context. Owned `ExecuteAsync` completes
after materialization and disposal of the temporary reply lease. `ExecuteLeaseAsync`
completes when it transfers the result lease to the caller, who must still dispose it.
Later caller-selected conversions (for example `result.AsDouble()`) are outside both
result boundaries, including conversion failures. Fire-and-forget completes after its
existing write/redirect handling. Retries, redirects, blocking dispatch, cluster-wide
mutation legs and their mutation fences remain within one admitted operation.
Hooks do not change replay eligibility: interpolated commands retain their raw-command
restriction against retry-policy replay; cluster redirects keep their existing behavior.

### Functions

Immediate `Functions.ExecuteAsync`, `ExecuteSpanAsync`, `ExecuteLeaseAsync`,
`ExecuteLeaseSpanAsync`, `ExecuteIntegerAsync`, `ExecuteStringAsync` and typed
`ExecuteAsync<T>` admit one `FCALL` or `FCALL_RO` after key/argument preflight and
before the first send. Completion includes declared retries, missing-library
`FUNCTION LIST`/`LOAD` recovery, replica propagation, and result conversion and cleanup.
Materialized results complete after copying into owned storage; leased results complete
when ownership transfers to the caller, who must still dispose them. Later caller-selected
conversions and disposal do not invoke the hook again.

`Functions.LoadAsync`, `DeleteAsync`, `FlushAsync` and `RestoreAsync` admit one
`FUNCTION LOAD`, `FUNCTION DELETE`, `FUNCTION FLUSH` or `FUNCTION RESTORE` respectively.
Cluster fan-out, per-target retries, reply conversion, consistency checks and owned cleanup
remain inside that scope. Completion waits for every started leg, including when one fails.
`ListAsync`, `DumpAsync` and `StatsAsync` use the ordinary converted-response boundary.
Internal library recovery does not create extra function-mutation hooks. Application calls
started by callbacks retain their own independent scopes. Batch and transaction function
members remain governed by the separate pipeline hook.

### Streaming and subscription resources

- `Strings.SetAsync` with a `Stream` or `ReadOnlySequence<byte>` admits one `SET`
  before reading the source or dispatching. Completion follows the final reply conversion,
  mutation fence and dedicated-connection cleanup, including source failures and cancellation.
  The caller still owns the source; Respire does not dispose it.
- Large array-backed value `SET` calls also complete only after all accepted socket writes
  release their borrowed memory. A cancellation or early reply cannot invoke completion
  while a gathered write still uses the buffer. The callback may therefore reuse the buffer.
- `Strings.GetStreamAsync` admits one `GET` before dispatch and completes when it returns
  the stream or null, or fails to acquire the result. This is a **stream-transfer boundary**,
  not a measurement of payload consumption. Later read failures, read cancellation, EOF and
  stream disposal do not invoke the hook again. The caller must consume or dispose the
  stream to release its dedicated connection, just as without a hook.
- `SubscribeAsync` with ordinary, `RespireChannel.Pattern`, or `RespireChannel.Sharded`
  targets admits one logical
  `SUBSCRIBE`, `PSUBSCRIBE` or `SSUBSCRIBE` for the requested targets. Completion follows
  activation and acknowledgement, or failure cleanup. Explicit `RespireSubscription.DisposeAsync`
  admits the corresponding unsubscribe operation before ending enumeration or removing routes,
  and completes after teardown. Rejection leaves the subscription live and disposal can be
  retried. Duplicate, concurrent or callback-reentrant disposal does not create another scope
  while disposal admission is active; a duplicate call need not await the first disposal.

Subscription hooks describe resource operations even when a shared route needs no wire
unsubscribe. Client disposal, reconnect/resubscribe, topology recovery and message delivery
are housekeeping, not new application operations. The setup hook does not remain open for
the subscription's lifetime. Teardown has no cancellation token; setup uses its caller token.

### Specialized server and cluster operations

Specialized server operations are also covered: pinned client/HOTKEYS commands, `CLIENT ID`
handle acquisition, explicit `OnNode` operations, server facet conversions and server fan-out.
Explicit-node completion includes connection-pool disposal and mutation-fence cleanup.
Cluster `DBSIZE`, `FLUSHDB`/`FLUSHALL`, and `SCRIPT LOAD`/`EXISTS`/`FLUSH` have one scope
around all targets and their final conversion or reduction. Server fan-out APIs return
per-node failures as data: publishing their result array is `Succeeded`, even if entries
contain conversion errors or cancellation. Discovery errors that escape the operation are
reported as `Failed` or `Canceled`.

`ScanClusterPageAsync` has one `SCAN` scope per page, including resumable Valkey
`CLUSTERSCAN`, redirects, capability fallback and final checkpoint conversion. Its topology,
run-ID and capability probes do not generate separate callbacks. Direct
`ScanValkeyClusterPageAsync` uses a `CLUSTERSCAN` scope. A completed cursor still receives
admission and completion when asked for another page, even though it sends no command.

### Stream appends

`Streams.AddAsync` admits one `XADD` and completes after publishing its stream ID or
nullable result. Automatic producer identity, lost-reply retries, and fallback when an
older server rejects IDMP all belong to that same scope. A null reply to an automatically
idempotent required-result overload is `Failed`; a null optional result is `Succeeded`.
Internal attempts do not create callbacks, while application commands started by a hook
receive their own independent scopes. Completion follows the existing mutation fences
and response cleanup. Ordinary appends and explicitly supplied IDMP options also finish
their typed conversion inside the scope. Argument validation and command construction
remain preflight work before admission.

### Scripts, blocking replies and cursor pages

Script-object `Scripts.ExecuteAsync`, `ExecuteSpanAsync`, `ExecuteLeaseAsync`,
`ExecuteLeaseSpanAsync`, and the typed/integer/string execution conveniences use one
`EVALSHA` (or `EVALSHA_RO`) scope. It encloses NOSCRIPT fallback to EVAL, declared
retries, cache mutation cleanup, and the selected result conversion. Both callbacks retain
the `EVALSHA` or `EVALSHA_RO` command name even when fallback sends `EVAL` or `EVAL_RO`.
Owned results finish materialization before completion; leased results complete when the
lease is returned to the caller. The caller's subsequent conversion or disposal of that
lease is outside the hook boundary.

Dedicated blocking list, sorted-set, and stream commands that use the blocking converted
response helper complete after reply conversion and dedicated connection cleanup. Their
internal connection setup and cancellation control commands do not create hook events.
Continuous `ReadGroupAsync` enumerations with a configured hook admit an initially
canceled call before reporting cancellation. The single-stream overload that accepts
`startAt` drains an already fetched batch and then ends quietly if canceled, with or
without a hook; it does not admit another page. The `StreamReadOptions` overload checks
cancellation between entries and throws outside the already completed page scope.

Ordinary `Keys.ScanAsync` and collection SSCAN/HSCAN/ZSCAN enumerations admit each
requested page separately. Completion includes parsing that page, before its items are
yielded. Rejecting a later page prevents its send while leaving earlier pages delivered.
Cancellation between yielded items is outside the already completed page.

The following paths do not yet have a complete hook contract:
multi-key cache reads, hash partial reads, custom facet conversions, imports, and composite helpers
such as managed locks or cache-aside factories. Some invoke covered helpers internally;
that does not make their entire public operation covered. Explicit batch and transaction
members also remain outside this command hook. Use the separate
[pipeline hook](batches-and-transactions.md#observe-or-reject-explicit-executions) for
aggregate admission and completion.

Tracked scripts and correction sends belong to the surrounding lock, cache or coordination
operation. Their internal dispatch helpers deliberately do not create scopes; admitting a
transport leg would miss earlier setup and finish before correction cleanup. Imports and
diagnostic aggregates such as `ProbeHealthAsync` also require the remaining composite audit.
This coverage does not establish a client-wide blocking policy.

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
