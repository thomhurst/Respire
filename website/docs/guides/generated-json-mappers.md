---
title: Generated JSON mappers
description: Reflection-free JSON model codecs, key templates, and typed RedisJSON property operations.
---

# Generated JSON mappers

Install `Respire.Json`. Its dependency on `Respire` supplies the source generator automatically.
Apply `[RespireJson]` to a partial class or record class to generate `ModelNameJsonMapper` in the
model's namespace. The mapper exposes JSON codecs, a key template, and typed RedisJSON operations
without runtime reflection. It supports .NET 8 and .NET 10, including Native AOT consumers.

<!-- doc-test-top-level-tail-declaration: split-before=[RespireJson -->
```csharp
using Respire;
using Respire.Json;

var user = new User("42", "Ada", null);
RespireKey key = UserJsonMapper.GetKey(user); // user:{42}
byte[] json = UserJsonMapper.ToJson(user);
User? copy = UserJsonMapper.FromJson(json);

[RespireJson("user:{{{Id}}}")]
public partial record User(string Id, string? Name, int? Age);
```

The generated `JsonTypeInfo` property works with `JsonSerializer` and every metadata overload on
`RespireJsonClient`. The generator emits converters and `JsonTypeInfo<T>` metadata through
`JsonMetadataServices`; no separate `JsonSerializerContext` or reflection resolver is required.
The generated options use an empty resolver, so unsupported types cannot fall back to reflection.
Serialization options and custom converters are not configurable on the mapper.

## RedisJSON requirements and operations

Redis I/O requires a server providing `JSON.GET`, `JSON.SET`, and JSONPath (`$`) support, such as
Redis 8 or Redis Stack with RedisJSON 2 or later. A plain server without that module returns an
unknown-command server error. The local codec methods do not require Redis.

Given a `RespireJsonClient jsonClient`, write a document with
`await UserJsonMapper.SetAsync(jsonClient, user)` and read it with
`await UserJsonMapper.GetAsync(jsonClient, key)`. Update one property with
`await UserJsonMapper.SetNameAsync(jsonClient, key, "Grace")` and read it with
`await UserJsonMapper.GetNameAsync(jsonClient, key)`. Nullable writes such as
`await UserJsonMapper.SetAgeAsync(jsonClient, key, null)` store JSON null.

`SetAsync(client, model)` expands the model key template. The explicit-key overload,
`SetAsync(client, key, model)`, also accepts a null model to store a JSON null document.
Both overloads accept `RespireJsonSetCondition` (`None`, `Nx`, `Xx`) and return false when a
conditional write is rejected. Reads accept explicit keys. The client's key prefix applies to
all generated operations. An explicit key does not need to match the model template.

Every mapped property has `PropertyNamePath`, `GetPropertyNameAsync`, and `SetPropertyNameAsync`.
These operate on one top-level property with generated scalar metadata. Paths use JSONPath bracket
notation and escape quotes and backslashes in storage names. RedisJSON requires literal Unicode and
control characters in paths rather than JSON string escape sequences such as `\u` or `\n`; the mapper
preserves those characters. Dots and apostrophes in a `[JsonPropertyName]` name are also literal characters.
These generated paths are fixed;
use `RespireJsonClient` with explicit metadata for arbitrary or nested paths and wildcard reads.

- Missing keys or JSONPath matches return `Found = false`.
- Stored JSON null returns `Found = true` and the default value. For nullable properties and reference
  models, that value is null. For non-nullable value types, it is the scalar default (for example, zero).
- A generated read returning multiple matches throws `InvalidOperationException`; malformed or
  incompatible JSON throws `JsonException`.
- Partial writes can create a missing property inside an existing object, but cannot create a
  missing document. The module's path/type errors surface as `RespireServerException`.
- Every I/O method accepts `CancellationToken` and forwards it to the underlying client. Cancellation
  after submission does not promise that Redis did not apply a write.

## Supported shapes and JSON rules

Models must be public or internal, top-level, non-generic, non-abstract partial classes or record
classes without inheritance. Public instance properties need public get and set/init accessors.
Supported types match [generated hash codecs](generated-hash-codecs.md): `string`, `bool`, `int`,
`long`, `double`, `decimal`, `Guid`, and `DateTimeOffset`, including nullable variants. Positional
records and accessible constructors whose parameter names and types match properties are supported.
Construction and member access are emitted directly. Private state is not serialized.

Property names are case-sensitive. `[JsonPropertyName]` changes the storage name and generated path;
key placeholders still use the C# property name. All properties are written, including nulls.
Missing nullable properties read as null. Missing non-nullable properties and null values for
non-nullable properties in a model object throw `JsonException`. The C# `required` modifier controls
construction, while JSON missing/null rules follow property nullability. Unknown JSON members are
ignored; duplicate members use their last value. A null whole document is accepted by `FromJson`.

Numbers use JSON number syntax and invariant culture; numbers encoded as strings are rejected.
`Guid` and `DateTimeOffset` use System.Text.Json's string formats. Non-finite doubles cannot be
written. Invalid, truncated, out-of-range, or trailing JSON fails decoding. JSON codecs allocate
their model objects and serialized byte arrays; they do not promise zero allocation.

Key templates share hash mapper rules: `{Id}` inserts a non-nullable scalar property, `{{` and `}}`
insert literal braces, and numeric values use invariant formatting. A null model or a null required
string key property fails before I/O. Nullable placeholders and malformed templates are rejected.

`RESP005` reports unsupported shapes, inaccessible construction, invalid templates, mapper name
collisions, duplicate storage names, and unsupported serialization attributes. Collections, nested
objects, public fields, indexers, custom converters, `[JsonIgnore]`, `[JsonInclude]`, extension data,
and polymorphic/type serialization attributes are not supported. Invalid models emit no mapper.

Generated hash Redis I/O, field TTL, change tracking, Search mapping, and final mapper performance
acceptance remain tracked by [#895](https://github.com/thomhurst/Respire/issues/895).
