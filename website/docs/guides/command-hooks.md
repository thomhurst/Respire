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
streaming results, subscription setup/teardown, specialized server operations, cluster scan pages,
multi-key and partial hash cache reads, cache-aside factories, distributed-cache operations,
explicit pipeline/transaction members, WATCH setup, immediate hash imports,
import-session acquisition and explicit health-probe rounds.
Managed locks, semaphores, countdown latches and the tracked coordination leases described below are also covered.
This is **not a client-wide command-blocking policy**. Remaining composite coverage and
the final policy audit are tracked in [#1708](https://github.com/thomhurst/Respire/issues/1708).
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

### Cache operation owners

`Strings.GetManyAsync` (including typed overloads) admits one `MGET` before any cache
lookup. Completion includes combining resident and fetched values, retries, generation
rechecks, and every selected typed conversion. Raw Execute MGET calls keep their own
result boundary without admitting the internal cached read again. `Hashes.GetManyAsync`
uses one `HMGET`, including partial field hits and the remaining fetched fields;
`Hashes.GetAllAsync<T>` includes its dictionary and typed-value conversion in `HGETALL`.
Other cacheable facet reads that use the converted-response helpers retain that same
conversion boundary on both cache hits and misses.

`GetOrSetAsync<T>` admits one logical `GETORSET` after factory/expiry validation and
before cache lookup, tracking, or factory execution. Completion includes factory execution,
serialization, conditional insertion, mutation-fence cleanup, and final deserialization.
Internal GET and SET legs do not produce additional hook events. Calls made by the
application factory or a hook receive independent scopes. Each coalesced caller has its
own scope and cancellation outcome; canceling one caller can leave the shared producer
running for other callers. The boundary also applies to `WithoutClientCache` views.

`RespireDistributedCache` backed by a concrete `RespireClient` (including prefixed views)
admits `GET` for Get/TryGet, `SET` for both Set overloads, `REFRESH` for Refresh, and
`UNLINK` for Remove. Synchronous entry points use the same scopes. These are logical
adapter operations, even when the implementation sends scripts. Admission precedes
buffer coalescing, codecs and tracked setup. Completion includes decoding or destination
writes, foreground TTL corrections, and result/lease cleanup. Internal scripts, fallback
and correction sends do not create extra events. An opaque custom `IRespireClient`
decorator does not expose its underlying hook to the adapter and is outside this contract.

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
members use the aggregate member boundary described below.

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

### Imports, bulk writes and health probes

`Hashes.CreateImportSessionAsync`, with or without a routing key, admits the logical
operation `HIMPORT OPEN` after routing validation and before pool discovery or connection
setup. Completion transfers ownership of the dedicated session to the caller; it does not
span the session's lifetime. Rejecting acquisition opens no connection.

Immediate session `PrepareAsync`, `SetAsync`, `DiscardAsync` and `DiscardAllAsync` admit
`HIMPORT PREPARE`, `HIMPORT SET`, `HIMPORT DISCARD` and `HIMPORT DISCARDALL` respectively.
Arguments are validated and snapshotted before admission. Completion includes reply
conversion, mutation-fence release, sequential-usage release and any required session expiry.
Hook rejection and cancellation before dispatch keep existing fieldsets usable. Ordinary
server errors retain the session; uncertain sends still expire it without replay.

Session `DisposeAsync` is unconditional ownership cleanup, with no hook admission or
completion. It closes the dedicated connection and destroys its fieldsets even if a hook
would reject commands. Internal expiry uses that same cleanup. `CreateBatch` and
`CreateTransaction` only construct queues: their member dispatch belongs to the separate
aggregate contract, not these immediate-operation scopes.

Bulk writes already use ordinary converted-response scopes: `Strings.SetManyAsync`,
`SetManyIfNotExistsAsync` and `SetManyExpireAsync` expose `MSET`, `MSETNX` and `MSETEX`;
multi-field hash writes expose `HSET`/`HSETEX`, array multi-set exposes `ARMSET`, and key
restore exposes `RESTORE`. Each includes conversion and preserves existing atomicity and
expiry semantics. `Functions.RestoreAsync` uses the function-mutation boundary; explicit
server-node migration and cluster import use the server-node boundary. There is no separate
multi-command bulk-import helper. Application calls made from callbacks retain independent
scopes; internal import transport and cleanup do not create duplicate scopes.

`ProbeHealthAsync` admits one logical `PROBE HEALTH` operation after options validation
and before capturing existing connections or sending any probe. Completion includes all
started probes and publishing the owned result array. Per-node errors, unavailable nodes,
and probe-deadline failures are **data**: returning that array is `Succeeded`, even for mixed
or entirely failed results. Inspect each result's `Error` to determine health. Caller
cancellation is `Canceled`; snapshot/discovery failures that escape are `Failed`.
The round's internal PINGs, sampled health PINGs, topology and capability probes do not
produce additional command hooks. Explicit application PING calls keep their normal scope.

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
without a hook; it does not admit another page. The `RespireStreamReadOptions` overload checks
cancellation between entries and throws outside the already completed page scope.

Ordinary `Keys.ScanAsync` and collection SSCAN/HSCAN/ZSCAN enumerations admit each
requested page separately. Completion includes parsing that page, before its items are
yielded. Rejecting a later page prevents its send while leaving earlier pages delivered.
Cancellation between yielded items is outside the already completed page.

### Locks and tracked coordination

These composite operations use logical names, rather than the Redis command selected by
capability fallback. Rejecting `SET` or `EVALSHA` alone does not reject a lock operation.
Use the names in this table when applying a policy to these APIs.

| Public operation | Hook name |
| --- | --- |
| `Locks.AcquireAsync`, `AcquireOrThrowAsync`, `TryTakeAsync` (including polling and `keepAlive`) | `LOCK ACQUIRE` |
| Token-based or managed lock release, renewal, owner lookup/verification | `LOCK RELEASE`, `LOCK RENEW`, `LOCK VERIFY` |
| Managed lock disposal; explicit keep-alive start/stop | `LOCK DISPOSE`; `LOCK KEEPALIVE START`, `LOCK KEEPALIVE STOP` |
| Semaphore acquisition and lease verification, renewal, release, disposal | `SEMAPHORE ACQUIRE`, `SEMAPHORE VERIFY`, `SEMAPHORE RENEW`, `SEMAPHORE RELEASE`, `SEMAPHORE DISPOSE` |
| Immediate fenced lock acquisition; notification-assisted waiting | `LOCK ACQUIRE FENCED`; `LOCK WAIT FENCED` |
| Read/write lock acquisition | `LOCK ACQUIRE READ`, `LOCK ACQUIRE WRITE` |
| Read/write lock verification, renewal, release, disposal | `LOCK VERIFY READWRITE`, `LOCK RENEW READWRITE`, `LOCK RELEASE READWRITE`, `LOCK DISPOSE READWRITE` |
| Hash-field lease immediate acquisition; notification-assisted waiting | `COORDINATION LEASE ACQUIRE`; `COORDINATION LEASE WAIT` |
| Hash-field lease verification, renewal, release, disposal | `COORDINATION LEASE VERIFY`, `COORDINATION LEASE RENEW`, `COORDINATION LEASE RELEASE`, `COORDINATION LEASE DISPOSE` |
| Countdown latch creation, joining and reset | `LATCH CREATE`, `LATCH JOIN`, `LATCH RESET` |
| Countdown latch signal and wait | `LATCH COUNTDOWN`, `LATCH WAIT` |

Fenced lock handles delegate to managed locks and use their `LOCK` names for subsequent
operations. Admission precedes tracked connection setup, mutation fences, ownership changes
and notification waiting. One completion includes contention polling, native/Lua capability
fallback, final result conversion and foreground correction/cleanup. A failed
`AcquireOrThrowAsync` completes as `Failed`; an unsuccessful acquisition attempt is `Succeeded`.
Each caller joining a managed lock release receives its own scope around its result.

Rejecting release or disposal leaves the handle available for a later attempt; rejecting
keep-alive stop leaves renewal running. Completion reports the public API's outcome, including
its existing cleanup exception-swallowing rules. Disposal does not admit a nested release.
An unsuccessful acquisition attempt has no resource to dispose and creates no disposal scope.

Background renewal, detached cleanup retries, tracked script legs, `CLIENT ID`/`CLIENT KILL`
fences and notification tracking reads are housekeeping inside or following the owning operation;
they do not ask policy permission again. Bounded foreground cleanup can hand remaining recovery
to the existing background queue. Completion does not promise that detached recovery has ended.
Rejecting `LOCK RENEW` does not stop background keep-alive renewals; use acquisition or
`LOCK KEEPALIVE START` admission to control their start.
The acquisition scope ends when it returns the handle, not at the end of the lease's lifetime.
Application calls from callbacks still receive independent scopes. Coordination coverage applies
to a concrete `RespireClient`; custom `IRespireClient` decorators retain their own dispatch behavior.

Countdown latch admission precedes script dispatch and notification setup. Creation and
joining complete when the handle (or a missing-generation null result) is returned; they
do not span the generation's lifetime. Reset and count-down include the selected script's
result handling. Joining and waiting include state validation and compatibility fallback
on servers without read-only script commands. Invalid state or count conversion completes
as `Failed`, rather than reporting a successful script leg.

One `LATCH WAIT` scope includes repeated state reads, notification subscription, periodic
resynchronization and foreground subscription/enumerator teardown. Reaching zero or finding
a replaced/removed generation completes as `Succeeded` with the existing boolean result.
Cancellation completes as `Canceled` after teardown. The wait's script, SUBSCRIBE and
UNSUBSCRIBE legs do not invoke hooks: rejecting a separate application unsubscribe cannot
prevent the wait from cleaning up its own subscription. Callback-initiated application
commands still have independent scopes. Initially canceled calls are admitted before
cancellation is reported, without sending commands. These boundaries apply to concrete
clients and their views; opaque custom client decorators retain their own dispatch behavior.
`CountdownLatchCommandHookTests` covers no-wire rejection for all five owners, final
conversion, compatibility fallback, wait teardown, cancellation and application reentrancy.

This extends the tracked-owner inventory for [#1755](https://github.com/thomhurst/Respire/issues/1755)
with [#1767](https://github.com/thomhurst/Respire/issues/1767). Rate limiters
([#1768](https://github.com/thomhurst/Respire/issues/1768)) and multi-client Redlock groups
([#1769](https://github.com/thomhurst/Respire/issues/1769)) still require their own composite
boundaries. Their lower-level hooks do not establish a once-only outer operation contract.

The following paths also do not yet have a complete hook contract:
remaining custom facet conversions and composite helpers.
Some invoke covered helpers internally;
that does not make their entire public operation covered. The final cross-route audit is
tracked in [#1708](https://github.com/thomhurst/Respire/issues/1708).

Tracked scripts and correction sends belong to the surrounding lock, cache or coordination
operation. Their internal dispatch helpers deliberately do not create scopes; admitting a
transport leg would miss earlier setup and finish before correction cleanup.
This coverage does not establish a client-wide blocking policy.

### Explicit aggregate members and WATCH

`CommandHook` admits batch and transaction members when the aggregate executes, not when
commands are queued. When both hooks are configured, `PipelineHook.OnExecuting` runs first,
then each `CommandHook.OnExecuting` in queue order, with `WAIT` or `WAITAOF` last for a
durability batch. All admissions finish before connection acquisition or the first member
send. With a command hook, caller cancellation is checked after admission. Structural and
lifecycle validation still precede callbacks.

An aggregate-hook rejection invokes no member hooks. A member rejection stops further
admission and faults **every** queued pending with that same exception. The rejecting member
completes as `Rejected` with null state; previously admitted members complete as `Failed`
(or `Canceled` for a cancellation exception). Members whose admission was never entered
receive no callback. `TryExecuteAsync` returns the pending failures; throwing execution APIs
retain their existing error behavior. The aggregate hook sees a member rejection as a failed
or canceled execution, rather than rejection of its own admission.

Entered members complete in queue order after final reply conversion, retries, mutation
fences and owned cleanup, outside an import session's usage gate. The pipeline hook completes
last. Each member reports its own pending outcome, so a failed `WAIT`/`WAITAOF` does not turn
successful writes into failures. An admitted acknowledgement skipped after a member failure
reports the execution failure. An aborted watched transaction reports `Failed` with
`RespireTransactionAbortedException` for each entered member, matching inspection of an
aborted pending; the pipeline hook retains its `Aborted` outcome. `MULTI`, `EXEC`, internal
retry attempts and protocol housekeeping do not create command events. Empty aggregates
have no command events, while their pipeline hook still runs.

Creating a watched transaction with nonempty keys admits one independent `WATCH` scope
after key snapshot/routing validation and before dedicated connection acquisition. It
completes on transfer of the watched transaction, or after failed-setup cleanup. Rejection
sends no WATCH. Creating with no watch keys has no WATCH scope. Commit and disposal retain
their existing connection ownership; rejecting a commit retires its WATCH connection
without sending MULTI/EXEC. Each fresh watched transaction created by the application-level
retry helper is a new WATCH and member execution, unlike internal retries of one commit.

`AggregateCommandHookTests` covers both hook combinations, no-wire rejection, final
conversion failures, cancellation, empty aggregates, retries, watched aborts, durability
and import member cleanup. This extends the ordinary (#1685) and specialized (#1707)
boundaries; it does not establish client-wide coverage for the remaining composite owners.

## Order and outcomes

For ordinary commands, after argument construction and preflight validation, `OnExecuting` runs synchronously
before the covered operation dispatches or reads the cache. It can return any state object.
Composite coordination owners admit before entering their operation; validation performed
inside that operation is reported as a failed admitted call.
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
