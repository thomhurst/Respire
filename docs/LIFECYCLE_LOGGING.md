# Typed lifecycle logging

Core lifecycle diagnostics use `RespireLog`; the OutputCaching package uses
`OutputCacheLog`. Their named methods call cached, typed `LoggerMessage.Define`
delegates. This keeps the existing message templates, structured property names,
levels, exception objects and logger categories. It also preserves
unnamed event zero and the named credential-refresh and coordination-cleanup
event identities. The existing generated Sentinel missing-epoch message retains
its event identity.

`LoggerMessageAttribute.EventName` defaults to the method name, even when an
explicit `EventId = 0` is supplied. That differs from the existing unnamed event
zero, so the converted catalog retains `LoggerMessage.Define` delegates rather
than changing event names through source generation.

The delegates check `IsEnabled` before creating log state. Value types stay typed
until an enabled provider chooses to enumerate or format the state. Cache
initialization occurs once; disabled calls do not create a params array or box
their arguments. Formatting that can allocate, decode a reply, inspect an
exception message or count pending tasks belongs behind an enabled-level check
inside the typed method. Null-conditional calls continue to skip null loggers.

Keep each diagnostic within its existing failure boundary. Pub/sub's `TryLog`
preserves its exception filter, Sentinel's `SafeLog` preserves its recoverable
exception policy and guarded-failure metric, and topology's generic `TryLog`
preserves its catch-all policy. These helpers pass typed state to static callbacks
instead of accepting arbitrary templates and `object[]` arguments. They do not
hide operational work inside a logging callback.

The ordinary reconnect retry, configured recovery-attempt failure, and replacement
cleanup failure also use Pub/sub's `TryLog` boundary. A nonfatal provider failure
must not strand resubscription; `OutOfMemoryException` retains the existing fatal
exception policy.

When a diagnostic gains an exception, update its typed wrapper signature and
forward the original exception through the delegate's exception parameter. An
exception used only to produce a structured message property is different: keep
that formatting behind `IsEnabled` and preserve whether the legacy event carried
an exception object.

Source projects enforce CA1848 and CA1873 as errors through `src/.editorconfig`.
New logs should follow the same typed pattern and guard expensive argument
evaluation. Logging tests compare representative events with their previous
structured output, exercise provider failures in `Log` and `IsEnabled`, and use
the repository allocation helper with warm-up and a positive control.

`LifecycleLogContract.json` pins all 140 cached core message delegates to the
pre-conversion commit and source locations. Contract tests compare levels, event
IDs and names, templates, structured properties, formatted output and exception
identity against legacy logging calls, and verify disabled delegates skip `Log`.
The coverage check rejects missing or newly added delegates until their contract
is recorded. Separate tests cover all seven dynamic Sentinel levels, wrapper
argument transformations, and the two OutputCaching messages. Update the snapshot
only for an intentional logging-contract change, not to accept a failing test.
