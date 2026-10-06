# Typed lifecycle logging

Core lifecycle diagnostics use `RespireLog`; the OutputCaching package uses
`OutputCacheLog`. Their named methods call cached, typed `LoggerMessage.Define`
delegates. This keeps the existing message templates, structured property names,
levels, exception objects and logger categories. It also preserves
unnamed event zero and the named credential-refresh and coordination-cleanup
event identities. The existing generated Sentinel missing-epoch message retains
its event identity.

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

Source projects enforce CA1848 and CA1873 as errors through `src/.editorconfig`.
New logs should follow the same typed pattern and guard expensive argument
evaluation. Logging tests compare representative events with their previous
structured output, exercise provider failures in `Log` and `IsEnabled`, and use
the repository allocation helper with warm-up and a positive control.
