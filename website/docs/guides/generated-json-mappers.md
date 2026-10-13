---
title: Generated JSON mappers
description: Reflection-free JSON model codecs, key templates, and typed RedisJSON property operations.
---

# Generated JSON mappers

Apply `[RespireJson]` to a partial class or record class to generate `ModelNameJsonMapper`: JSON
codecs, a key template, and typed RedisJSON operations, with no runtime reflection. Install
`Respire.Json`; its `Respire` dependency supplies the source generator. Supports .NET 8 and .NET 10,
including Native AOT.

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

The mapper lives in the model's namespace. Its generated `JsonTypeInfo` property works with
`JsonSerializer` and every metadata overload on `RespireJsonClient`. No `JsonSerializerContext` is
needed. The generated options use an empty resolver, so unsupported types cannot fall back to
reflection. Serialization options and custom converters are not configurable.

## RedisJSON requirements and operations

Redis I/O needs `JSON.GET`, `JSON.SET`, and JSONPath (`$`), as in Redis 8 or Redis Stack with
RedisJSON 2 or later. Without the module, Redis returns an unknown-command error. The local codec
methods do not need Redis.

Pass a `RespireJsonClient`, for example `client.Json` (see [Redis JSON](json.md)):

| Call | Effect |
| --- | --- |
| `UserJsonMapper.SetAsync(jsonClient, user)` | Writes the document at the template key |
| `UserJsonMapper.SetAsync(jsonClient, key, user)` | Writes at an explicit key; a null model stores a JSON null document |
| `UserJsonMapper.GetAsync(jsonClient, key)` | Returns a `RespireJsonValue<User>` |
| `UserJsonMapper.SetNameAsync(jsonClient, key, "Grace")` | Writes one property |
| `UserJsonMapper.GetNameAsync(jsonClient, key)` | Reads one property |
| `UserJsonMapper.SetAgeAsync(jsonClient, key, null)` | Stores JSON null in a nullable property |

- Both `SetAsync` overloads take a `RespireJsonSetCondition` (`None`, `Nx`, `Xx`) and return false
  when a conditional write is rejected.
- The client's key prefix applies to all operations. An explicit key need not match the template.
- Every property gets `{Property}Path`, `Get{Property}Async`, and `Set{Property}Async`, for example
  `NamePath`. They work on one top-level property. For nested or arbitrary paths and wildcards, use
  `RespireJsonClient` with explicit metadata.
- Paths use JSONPath bracket notation and escape quotes and backslashes in storage names. Unicode
  and control characters stay literal, as RedisJSON requires (no `\u` or `\n` escapes). Dots and
  apostrophes in a `[JsonPropertyName]` name are literal too.

Results and errors:

- A missing key or match returns `Found = false`.
- Stored JSON null returns `Found = true` and the default value: null for nullable properties and
  reference models, the scalar default (such as zero) for non-nullable value types.
- A read with multiple matches throws `InvalidOperationException`. Malformed or incompatible JSON
  throws `JsonException`.
- Property writes can create a missing property in an existing object, but not a missing document.
  Module path/type errors throw `RespireServerException`.
- Every I/O method takes a `CancellationToken`. Cancellation after submission does not guarantee
  Redis did not apply a write.

## Supported shapes and JSON rules

Models:

- Public or internal, top-level, non-generic, non-abstract partial classes or record classes without
  inheritance.
- Public instance properties need public get and set/init accessors. Private state is not
  serialized.
- Types match [generated hash codecs](generated-hash-codecs.md): `string`, `bool`, `int`, `long`,
  `double`, `decimal`, `Guid`, and `DateTimeOffset`, including nullable variants.
- Positional records and accessible constructors whose parameter names and types match properties
  are supported.

JSON rules:

- Property names are case-sensitive. `[JsonPropertyName]` changes the storage name and path; key
  placeholders still use the C# name.
- All properties are written, including nulls.
- Missing nullable properties read as null. Missing or null non-nullable properties throw
  `JsonException`. The C# `required` modifier affects construction only.
- Unknown members are ignored; duplicate members use the last value. `FromJson` accepts a null
  document.
- Numbers use JSON number syntax and invariant culture; numbers in strings are rejected.
  Non-finite doubles cannot be written. `Guid` and `DateTimeOffset` use System.Text.Json's formats.
- Invalid, truncated, out-of-range, or trailing JSON fails decoding.
- Codecs allocate model objects and byte arrays.

Key templates follow the hash mapper rules: `{Id}` inserts a non-nullable scalar property, `{{` and
`}}` insert literal braces, and numbers use invariant formatting. A null model or null required
string key property fails before I/O. Nullable placeholders and malformed templates are rejected.

`RESP005` reports unsupported shapes, inaccessible construction, invalid templates, mapper name
collisions, duplicate storage names, and unsupported attributes. Invalid models emit no mapper.
Unsupported: collections (except indexed `float[]`/`double[]` vectors in
[generated Search schemas](generated-search-schemas.md)), nested objects, public fields, indexers,
custom converters, `[JsonIgnore]`, `[JsonInclude]`, extension data, and polymorphic/type attributes.

For hash storage with field TTL and change tracking, see [generated hash codecs](generated-hash-codecs.md);
for indexes, see [generated Search schemas](generated-search-schemas.md). The
[Native AOT conformance sample](generated-hash-codecs.md#native-aot-conformance-sample) runs
generated hash, JSON, and Search operations on `net8.0` and `net10.0` with reflection-based JSON
serialization disabled.
