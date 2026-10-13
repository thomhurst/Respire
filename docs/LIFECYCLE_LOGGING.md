# Typed lifecycle logging

Log through a named method on `RespireLog` (core) or `OutputCacheLog` (OutputCaching) that calls a
cached `LoggerMessage.Define` delegate. Do not log with templates and `object[]` arguments. Source
projects enforce CA1848 and CA1873 as errors through `src/.editorconfig`.

## Rules

1. **Use `LoggerMessage.Define`, not `[LoggerMessage]`.** `LoggerMessageAttribute.EventName` defaults to
   the method name even with `EventId = 0`, which would rename the existing unnamed event zero.
2. **Keep the contract.** Preserve message templates, structured property names, levels, exception
   objects, logger categories, unnamed event zero, and the named credential-refresh and
   coordination-cleanup events. The generated Sentinel missing-epoch message keeps its event identity.
3. **Guard expensive work.** The delegates check `IsEnabled` before creating state, so disabled calls
   allocate nothing and box nothing. Put anything that can allocate, decode a reply, read an exception
   message or count pending tasks behind an enabled-level check inside the typed method.
   Null-conditional calls still skip null loggers.
4. **Keep each failure boundary.** Pub/sub `TryLog` keeps its exception filter. Sentinel `SafeLog`
   keeps its recoverable-exception policy and guarded-failure metric. Topology's generic `TryLog`
   keeps its catch-all policy. These helpers pass typed state to static callbacks and must not hide
   operational work in a logging callback.
5. **Pub/sub recovery logs must not strand resubscription.** Reconnect retry, recovery-attempt failure
   and replacement-cleanup failure use Pub/sub `TryLog`. `OutOfMemoryException` stays fatal.
6. **Exceptions.** When a diagnostic gains an exception, update the wrapper signature and pass the
   original exception through the delegate's exception parameter. If an exception only feeds a message
   property, format it behind `IsEnabled` and keep whether the event carried an exception object.

## Contract tests

`LifecycleLogContract.json` pins all 140 cached core message delegates to the pre-conversion commit
and source locations.

- Contract tests compare levels, event IDs and names, templates, structured properties, formatted
  output and exception identity with legacy logging calls, and check disabled delegates skip `Log`.
- The coverage check rejects missing or new delegates until their contract is recorded.
- Separate tests cover all seven dynamic Sentinel levels, wrapper argument transformations and the two
  OutputCaching messages.
- Logging tests exercise provider failures in `Log` and `IsEnabled`, and use the repository allocation
  helper with warm-up and a positive control.

Update the snapshot only for an intentional logging-contract change, never to make a test pass.
