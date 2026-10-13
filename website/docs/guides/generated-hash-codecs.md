---
title: Generated hash codecs
description: Generate reflection-free Redis hash codecs, key templates, field TTLs, and change tracking with [RespireHash].
---

`[RespireHash]` generates a reflection-free codec for a partial class or record class, mapping it to
a Redis hash. The generator ships in the `Respire` package. Use it for scalar models you want to
store as hash fields, with optional field TTLs and change-tracked updates.

<!-- doc-test-top-level-tail-declaration: split-before=[RespireHash -->
```csharp
using Respire;

var user = new User("42", "Ada", null);
RespireKey key = UserHashMapper.GetKey(user); // user:{42}
Dictionary<string, string> fields = UserHashMapper.ToFields(user);
User copy = UserHashMapper.FromFields(fields);

[RespireHash("user:{{{Id}}}")]
public partial record User(string Id, string Name, string? SessionToken);
```

- The companion `ModelNameHashMapper` lives in the model's namespace with the same public or
  internal accessibility.
- `ToFields` returns a new mutable dictionary with ordinal field names.
- `FromFields` takes an `IReadOnlyDictionary<string, string>`; use an ordinal dictionary for
  case-sensitive field names. Unknown fields are ignored.

## Values and missing fields

Public instance properties of these types are supported, including nullable variants. Field names
are property names.

| Property type | Stored text |
| --- | --- |
| `string` | Unchanged text, including empty strings and Unicode |
| `bool` | `1` or `0`; other representations are rejected when reading |
| `int`, `long`, `decimal` | Invariant numeric text |
| `double` | Invariant round-trip text, including .NET's `NaN` and infinity representations |
| `Guid` | `D` format |
| `DateTimeOffset` | `O` format, preserving the offset |

- Null properties are omitted. Empty strings are stored, so null and empty round-trip separately.
- A missing nullable field decodes to null. A missing non-nullable field throws `FormatException`.
- Malformed text throws `FormatException` or `OverflowException`. Numeric parsing is invariant;
  integers allow a leading sign, `double` and `decimal` also allow a decimal point and exponent.
  Whitespace and thousands separators are rejected.
- A runtime null in a non-nullable string property throws `ArgumentException` when encoding or
  expanding a key. Null codec arguments throw `ArgumentNullException`.
- Writing `ToFields` output with `HSET` cannot remove fields that became null. Use the generated
  `SetAsync` for full writes.

## Redis writes and reads

The companion provides `SetAsync`, `GetAsync`, and `GetPartialAsync` over `IRespireClient`:

<!-- doc-test-top-level-tail-declaration: split-before=[RespireHash -->
```csharp
using Respire;

await using var root = await RespireClient.ConnectAsync("localhost:6379");
var client = root.WithKeyPrefix("app:");
var user = new User("42", "Ada", null);

// Expands user:{42}, then applies the client's prefix once.
await UserHashMapper.SetAsync(client, user);
User? copy = await UserHashMapper.GetAsync(client, UserHashMapper.GetKey(user));

// An explicit key overrides the template; it does not need to match Id.
RespireKey binaryKey = new byte[] { 0xff, 0, 0x80 };
await UserHashMapper.SetAsync(client, binaryKey, user);
var partial = await UserHashMapper.GetPartialAsync(client, binaryKey,
    [nameof(User.Name), nameof(User.SessionToken)]);
bool requested = partial.SessionToken.Selected; // true
bool exists = partial.SessionToken.Found;       // false
string? name = partial.Name.Value;              // Ada
bool idRequested = partial.Id.Selected;        // false

[RespireHash("user:{{{Id}}}")]
public partial record User(string Id, string Name, string? SessionToken);
```

- `SetAsync(client, model)` uses the template; `SetAsync(client, key, model)` uses an explicit
  `RespireKey`, including empty, binary, and Unicode keys.
- Read methods take an explicit key; call `GetKey` when you have a model.
- The client's key prefix applies once. Binary key bytes are copied before I/O.

### Full write (`SetAsync`)

Without `[RespireFieldTtl]`, a full write sends:

1. One `HSET key field value ...` for non-null mapped properties.
2. One `HDEL key field ...` for mapped properties that are null.

Commands with no fields are skipped. Unknown fields are preserved. No `DEL`, existence check, or
full read is sent. An all-null model with no unknown fields leaves no hash, so a later `GetAsync`
returns null. These methods do not set a key TTL.

### Full read (`GetAsync`)

`GetAsync` sends one `HGETALL`. A missing hash returns null. Missing non-nullable properties throw
`FormatException`; missing nullable ones become null. A hash with only unknown fields still exists:
it decodes an all-nullable model but fails required properties. The read does not check that stored
properties expand to the supplied key.

### Partial read (`GetPartialAsync`)

`GetPartialAsync` sends one `HMGET` in the requested order and decodes only the selected
properties. Pass at least one distinct mapped property name with exact casing, ideally
`nameof(Model.Property)`. Null, unknown, and repeated names throw `ArgumentException` before I/O.
Each property is a `RespireHashField<T>`:

| State | `Selected` | `Found` | `Value` |
| --- | --- | --- | --- |
| Not requested | false | false | default |
| Requested, absent | true | false | default |
| Requested, present | true | true | decoded value |

- Empty strings and stored zero stay distinct from absent values.
- Missing non-nullable properties do not fail. Malformed selected text still throws
  `FormatException` or `OverflowException`; unselected fields are not decoded.
- The model is not constructed, and no constructors or accessors run.
- A missing hash returns every selection as absent, indistinguishable from a hash missing those
  fields.

### Atomicity, cancellation and malformed replies

- **A full write with several commands is not atomic.** Readers and writers can run between
  commands, and concurrent writes can mix model versions. There is no transaction, optimistic
  concurrency, or lock; coordinate writes externally when you need a consistent replacement.
- These reads use the raw command path and do not reuse [cached hash fields](../fundamentals/client-side-caching.md#partial-hash-reads).
- Every operation takes an optional `CancellationToken`. A pre-canceled token sends nothing.
  Cancellation or an error after `HSET` can leave a write partly applied; the mapper does not roll
  back or replay it. Normal client retry policies apply per command. Cancelling an accepted command
  does not guarantee Redis did not run it.
- Keep model properties stable while encoding; custom accessors run normally.
- Malformed replies throw `RespireProtocolException`. Full reads need complete RESP2 pairs or a
  RESP3 map with unique bulk-string names and values. Partial reads need an array with one bulk
  string or null per selection. Write replies need an integer from zero to the submitted field count.
- Redis errors, including `WRONGTYPE`, throw `RespireServerException`.

## Keys and supported models

Key templates:

- `{Property}` substitutes a non-nullable mapped property by exact name. Placeholders can repeat.
- `{{` and `}}` are literal braces, as in the Cluster hash tag above.
- Values are inserted unchanged: no escaping and no client prefix. A constant non-empty template is
  also allowed.

Models:

- Top-level, non-generic, public or internal partial classes or record classes, with no base class
  other than `object`.
- Every public instance property needs public `get` and `set`/`init` accessors. Property names must
  be distinct ignoring case. Private members and static properties are not mapped.
- There is no opt-out attribute, so a computed get-only property, collection, or nested object
  prevents generation. Use a separate scalar model. The exception is indexed `byte[]` vectors in
  [generated Search schemas](generated-search-schemas.md).
- Public fields, indexers, unsupported property types, abstract classes, record structs, and nested
  models produce diagnostic `RESP004`. A pre-existing `ModelNameHashMapper` type is rejected.

Constructors:

- May be `public`, `internal`, or `protected internal`. An accessible parameterless constructor is
  preferred.
- Otherwise, parameters must match public properties by name (ignoring case) and exact type,
  including nullability. The matching constructor with the fewest parameters wins; positional
  records qualify. Remaining properties are set in an object initializer.
- Constructor-bound properties are also assigned when C# required-member rules demand it, unless
  the constructor has `[SetsRequiredMembers]`.

## Field expiration

Apply `[RespireFieldTtl(milliseconds)]` to a mapped property. The duration must be positive
(otherwise `RESP004`). On positional records use the `property:` target.

<!-- doc-test-top-level-tail-declaration: split-before=[RespireHash -->
```csharp
using Respire;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var user = new ExpiringUser("42", "Ada", "session");
await ExpiringUserHashMapper.SetAsync(client, user);

// Explicit opt-in for Redis 7.4, where HSETEX is unavailable.
await ExpiringUserHashMapper.SetWithExpiryAsync(client, RespireHashExpiryMode.HSetThenExpire, user);

[RespireHash("user:{Id}")]
public partial record ExpiringUser(string Id, string Name,
    [property: RespireFieldTtl(60000)] string? SessionToken);
```

- TTL applies to the field, not the hash key. Each write refreshes expiry only for fields it writes.
  Null properties are removed without expiry.
- Expiration can remove required properties, and full reads then throw `FormatException`. Use
  nullable expiring properties or partial reads.
- Reads and no-op tracked updates do not refresh TTL or recreate expired fields.
- `SetAsync` always uses `HSetEx`. `SetWithExpiryAsync(client, expiryMode, model)` and
  `SetWithExpiryAsync(client, key, expiryMode, model)` choose the mode.

| Mode | Redis | Commands | Atomic |
| --- | --- | --- | --- |
| `HSetEx` (default) | [8.0+](https://redis.io/docs/latest/commands/hsetex/) | `HSETEX key PX ms FIELDS count field value ...` per duration, then `HSET` and `HDEL` | Each expiry group only |
| `HSetThenExpire` | [7.4+](https://redis.io/docs/latest/commands/hpexpire/) | `HSET`, then `HPEXPIRE` per expiry group | **No** |

Capability checks:

- Before every write with expiring fields, the mapper sends `COMMAND INFO HSETEX` or
  `COMMAND INFO HPEXPIRE` to every discovered node, including replicas. Allow `COMMAND INFO` in
  your ACLs.
- Missing support throws `NotSupportedException` without writing. Other metadata and ACL errors
  propagate.
- There is no automatic downgrade, capability cache, or silent write without expiry. Topology
  changes after the check can still cause a server error.

`HSetThenExpire` risks:

- Cancellation, expiry failure, or concurrent deletion can leave a field written but **persistent**.
- A non-success status throws `InvalidOperationException` naming the field and status; `-2` means
  the field disappeared first. Malformed status replies throw `RespireProtocolException`.
- Coordinate writers externally and retry the whole operation after failure.

Direct `SetAsync`/`SetWithExpiryAsync` and `RespireHashModelIO.WriteAsync` keep no retry state. Use
a tracker when retries must correct every possibly applied field.

## Change-tracked updates

A tracker writes only the fields that changed since the last successful update.

<!-- doc-test-top-level-tail-declaration: split-before=[RespireHash -->
```csharp
using Respire;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var user = new TrackedUser("42", "Ada", "session");
var key = TrackedUserHashMapper.GetKey(user);

// No baseline: first update writes all mapped properties, including null removals.
var tracker = TrackedUserHashMapper.Track(client, key);
await tracker.UpdateAsync(user);
await tracker.UpdateAsync(user); // No Redis commands.
await tracker.UpdateAsync(user with { Name = "Grace", SessionToken = null });

// A supplied baseline is assumed to be persisted; Track performs no I/O.
var existing = TrackedUserHashMapper.Track(client, user);
await existing.UpdateAsync(user with { Name = "Grace" }); // Only Name is written.

[RespireHash("user:{Id}")]
public partial record TrackedUser(string Id, string Name, string? SessionToken);
```

Creating a tracker:

- `Track(client, key, baseline: null, expiryMode: ...)` takes an explicit key and supports creation.
  `Track(client, baseline, expiryMode: ...)` expands the baseline's template key.
- The tracker is bound to that client and key (binary bytes copied). Changing a key-template
  property later changes its field but does not move the hash; create a new tracker for another key.
- The tracker keeps a snapshot of encoded strings, not your model, so mutating the baseline does not
  change it.

Each update:

- Encodes the model before I/O and writes only fields that differ from the snapshot. A field changed
  to null sends `HDEL`. Creation sends a full write. Unknown fields are preserved.
- An unchanged model sends no reads, writes, or capability queries.

Failure and retry:

- The snapshot advances only after **all** commands succeed.
- After a failed write, the tracker remembers every possibly applied field and resends them next
  time, even if you revert to the old values. Retrying the same model resends the whole delta.
- Retries can refresh TTL again and do not roll back partial writes. The tracker does not replay
  automatically; pass the model again.
- A successful write advances the snapshot even if caller cancellation is reported afterwards, so
  retrying it sends nothing.

Cancellation and timeouts:

- Cancellation before encoding or dispatch sends nothing, and can interrupt the capability check.
- Once a write group starts, cancellation waits for every command in it to settle before throwing,
  so a retry cannot overtake an accepted write.
- `RespireOptions.CommandTimeout` (default 10 seconds per command) still applies. Setting it to
  null removes the bound and can leave accepted writes waiting forever. Several command groups can
  each use the full timeout.
- **A timeout on a possibly submitted write invalidates the tracker.** Later updates throw
  `InvalidOperationException` without sending commands. Before resuming, make sure earlier commands
  have settled, then create a new tracker from freshly read values. Capability-check timeouts and
  definitive server errors stay retryable.

Concurrency:

- One tracker rejects overlapping `UpdateAsync` calls with `InvalidOperationException`.
- Separate trackers, clients, and external writers are not synchronized. The snapshot is your
  assumption about stored values, not a server-side concurrency token. External changes and field
  expiry do not update it; recreate the tracker from a fresh model to resynchronize.

For JSON documents, see [generated JSON mappers](generated-json-mappers.md).
[Generated Search schemas](generated-search-schemas.md) build on hash models, including binary
vector properties with field expiry and change tracking.

## Native AOT conformance sample

The [mapper sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Mappers)
publishes with trimming and Native AOT for `net8.0` and `net10.0`, and runs over RESP2 and RESP3
against Redis with JSON and Search modules. It covers scalar/null codecs, field TTL, tracked partial
updates, hash and JSON partial reads, and generated Search schemas with vector queries. Compiler and
linker warnings fail publishing, and reflection-based JSON serialization is disabled. See the
sample README for publishing commands and its codec-only mode.

The [mapper comparison workflow](https://github.com/thomhurst/Respire/blob/main/.github/workflows/benchmark-mapper.yml)
benchmarks generated hash, JSON, and Search conversion against handwritten code on .NET 10. It does
not measure Redis network, query, Native AOT, or cold-start performance.
