# Error metric foundation

`RespireTelemetry.RecordError` publishes `redis.client.errors` in the Resiliency
group. Selection is evaluated when the failure is handled or returned, rather
than when the operation starts. A throwing exporter cannot replace the application
failure. Instrument publication can recover after a rejecting listener is removed.

This foundation does not instrument command routes. Route integration must first
declare the final owner in the independent inventory tracked by #1241. Native
pooled results, shared producers and their callers, retry scopes, deferred results
and payload readers have distinct completion boundaries. Their integration is
tracked by #1023; the inventory must detect missing routes and removed owners.

Call `RecordError` only when an owner handles a failure internally or publishes
its final failure. Exception construction and physical connection failure do not
prove a logical command failed. An internally handled retry reports the number
of previous retries; final failure reports the completed retry count. Never wrap
a response whose delegated owner already publishes its final error.

`ObserveFinalError` consumes an exclusively owned `ValueTask` once. It checks
successful status without renting observation storage or consulting listeners,
then consumes the result because RESP sources can translate errors in `GetResult`.
Incomplete responses use a pooled async builder and `ConfigureAwait(false)`.
This helper is not a universal wrapper: shared, borrowed, native and deferred
results need their own ownership policy before using it. Successful paths must
not rent an observation solely for deduplication. Shared failure/retry storage and
borrower lifetime rules belong to #1241.

Classification follows only explicitly transparent `RespireException.ErrorCause`
wrappers and single-cause aggregates, with a defensive limit of 16 unwraps.
Authentication and timeout wrappers keep their meaning. Multi-cause connection
failures retain the connection category. Standard server codes use a bounded
allowlist; unknown Lua/module prefixes never become label values. Canonical type
names have a 128-name budget and use weak type keys, so collectible exception types
are not retained. Retry counts above the small boxing cache keep their exact value.
Messages, keys, values, credentials, query text and endpoint details are not tags.

The foundation tests cover cold publication recovery, successful-status error
translation, single consumption, original errors and cancellation tokens,
event-time selection, bounded labels and collectible types. Warmed no-inline
allocation controls use the process-wide no-GC boundary and an escaping positive
control described in `ALLOCATION_MEASUREMENT.md`.
