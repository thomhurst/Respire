---
title: Generated hash codecs
---

`[RespireHash]` generates a companion codec for a partial class or record class. The generator ships with the `Respire` package and emits direct property access, constructor calls and scalar parsing. It does not use reflection or dynamic serialization.

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

The companion lives in the model's namespace and has the same public or internal accessibility. `ToFields` returns a new mutable dictionary with ordinal field names. `FromFields` accepts an `IReadOnlyDictionary<string, string>`; use an ordinal dictionary for case-sensitive Redis field semantics. Unknown fields are ignored.

## Values and missing fields

Public instance properties support `string`, `bool`, `int`, `long`, `double`, `decimal`, `Guid` and `DateTimeOffset`, including nullable variants. Field names are property names.

| Property type | Stored text |
| --- | --- |
| `string` | Unchanged text, including empty strings and Unicode |
| `bool` | `1` or `0`; other representations are rejected when reading |
| `int`, `long`, `decimal` | Invariant numeric text |
| `double` | Invariant round-trip text, including .NET's `NaN` and infinity representations |
| `Guid` | `D` format |
| `DateTimeOffset` | `O` format, preserving the offset |

Null properties are omitted. Empty strings remain present, so null and empty values round-trip separately. A missing nullable field decodes to null. Missing non-nullable fields throw `FormatException`; malformed text throws the scalar parser's `FormatException` or `OverflowException`. A runtime null in a non-nullable string property throws `ArgumentException` when encoding or expanding a key. Null codec arguments throw `ArgumentNullException`.

Numeric decoding uses .NET's invariant-culture parsers with explicit styles. Integers allow a leading sign; `double` and `decimal` also allow a decimal point and exponent. Numeric text containing whitespace or thousands separators is rejected. Encoding always writes invariant numeric text without thousands separators.

`ToFields` produces a representation; it does not replace an existing Redis hash. Writing the returned fields with `HSET` alone cannot remove fields whose new value is null. Use the generated `SetAsync` methods for full writes that remove null mapped fields.

## Redis writes and reads

The companion provides `SetAsync`, `GetAsync` and `GetPartialAsync` over `IRespireClient`. These methods use the generated scalar codec, without reflection or dynamic serialization.

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

`SetAsync(client, model)` uses the template. `SetAsync(client, key, model)` uses the explicit `RespireKey`, including empty, binary and Unicode keys. Both read methods take an explicit key; call `GetKey` when a model is available. The client's text or binary key prefix applies once through the normal command path. Binary key bytes are copied before Redis I/O, keeping the same key across a full write's commands.

A full write sends one `HSET key field value ...` for non-null mapped properties, followed by one `HDEL key field ...` for mapped properties whose value is null. Empty strings are stored, not removed. Commands with no fields are omitted. Unknown fields are preserved, including when all mapped properties are null. No `DEL`, existence query or full-hash read is issued. An all-null model without unknown fields leaves no Redis hash, so a later full read returns null. Hash TTL is not configured by these methods; normal Redis behavior applies when removing the last field deletes a key.

`GetAsync` sends one `HGETALL`. An empty reply (a missing hash) returns null. An existing hash with missing non-nullable properties fails with `FormatException`; missing nullable properties become null. Unknown fields are ignored by the codec. A hash containing only unknown fields still exists: it can decode an all-nullable model, but it cannot satisfy required properties. The read does not verify that stored properties expand back to the supplied key.

`GetPartialAsync` sends one `HMGET`, in the requested order, and decodes only selected properties. Select at least one distinct mapped property name with exact casing; null, unknown and repeated names throw `ArgumentException` before I/O. Use `nameof(Model.Property)` for field names. The generated result exposes a `RespireHashField<T>` for each property:

| State | `Selected` | `Found` | `Value` |
| --- | --- | --- | --- |
| Not requested | false | false | default |
| Requested, absent | true | false | default |
| Requested, present | true | true | decoded value |

This preserves empty strings and stored numeric zero separately from absent values. Missing selected non-nullable properties do not fail a partial read. Partial reads do not construct the model or invoke its constructors or property accessors. Selected malformed scalar text still throws `FormatException` or `OverflowException`. An entirely missing hash returns selected-but-absent properties; partial reads cannot distinguish that from an existing hash missing every selected field. Unselected malformed values are not read or decoded.

### Atomicity, cancellation and malformed replies

Each `HSET`, `HDEL`, `HGETALL` and `HMGET` is individually atomic on Redis. A full write containing both present and null properties is **not atomic**: readers and other writers can run between `HSET` and `HDEL`. Concurrent full writes can therefore mix model versions. These APIs provide no transaction, optimistic concurrency, lock or change tracking. Coordinate writes externally when a consistent replacement is required. A full or partial read observes its single Redis command, not a guarantee about subsequent writes. These generated reads use the raw command path and do not reuse cached hash fields.

Every operation accepts an optional `CancellationToken`. A pre-canceled token prevents Redis I/O. Cancellation or a server/protocol error after `HSET` can leave the write partially applied; the mapper does not roll back or replay the full write. Normal client command retry policies still apply to each command. Cancellation of an accepted command does not guarantee that Redis did not execute it. Keep model properties stable while their key and field values are encoded, since custom accessors execute normally.

Malformed wire replies throw `RespireProtocolException`: full reads require complete RESP2 pairs or a RESP3 map, unique field names and bulk-string names/values; partial reads require an array with exactly one bulk string or null per selection. Write replies require an integer between zero and the submitted field count. Redis errors, including `WRONGTYPE`, propagate as `RespireServerException`. Scalar parsing errors remain distinct from malformed wire structure.

## Keys and supported models

Templates substitute non-nullable mapped properties by their exact name. Repeated placeholders are supported. Use `{{` and `}}` for literal braces, including Redis cluster hash tags as in the example. Values are inserted unchanged; the codec does not escape property values or add a client key prefix. A constant non-empty key template is also supported.

Models must be top-level, non-generic, public or internal partial classes or record classes, with no base class other than `object`. Every public instance property needs public `get` and `set`/`init` accessors. Property names must be distinct ignoring case. Public instance fields, indexers, unsupported property types, abstract classes, record structs and nested models produce diagnostic `RESP004`.

There is no property opt-out attribute in this initial codec. A computed get-only property, collection or nested object therefore prevents generation; use a separate scalar model when those properties are needed elsewhere.

Constructors may be `public`, `internal` or `protected internal`, because the mapper lives in the same assembly. An accessible parameterless constructor is preferred. Otherwise, constructor parameters must match public properties by name (ignoring case) and exact type, including nullability; the matching constructor with the fewest parameters is selected. Positional records satisfy this rule. The codec passes decoded values to that constructor and assigns remaining properties in an object initializer. Constructor-bound properties are also assigned when C# required-member rules demand it; a constructor marked `[SetsRequiredMembers]` avoids that additional assignment. Custom constructors and accessors execute normally; their behavior remains the model author's responsibility. Private members and static properties are not mapped. A pre-existing `ModelNameHashMapper` type is rejected.

Scalar codecs, key templates and generated Redis hash I/O with partial reads are delivered for [the object mapper work](https://github.com/thomhurst/Respire/issues/895). Field TTL, change tracking, JSON mapping, Search schemas, Native AOT sample execution and benchmark parity remain separately tracked there. Direct code generation alone does not establish final Native AOT or performance acceptance.
