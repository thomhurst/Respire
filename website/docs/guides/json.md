---
title: Redis JSON
description: Typed RedisJSON commands with System.Text.Json metadata, JSONPath, and projections.
---

`Respire.Json` provides typed Redis JSON commands using caller-supplied `System.Text.Json` metadata,
so it needs no reflection and supports Native AOT. JSON is built into Redis Open Source 8 and later;
older deployments need Redis Stack or the RedisJSON module.

```bash
dotnet add package Respire.Json
```

<!-- doc-test-tail-declaration: split-before=[JsonSerializable -->
```csharp
using System.Text.Json.Serialization;
using Respire;
using Respire.Json;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var json = client.Json;
var key = (RespireKey)"customer:42";
var customer = new Customer("Ada", "ada@example.test");

await json.SetAsync(key, customer, CustomerJsonContext.Default.Customer);
var loaded = await json.GetAsync(key, CustomerJsonContext.Default.Customer);
if (loaded.Found)
    Console.WriteLine(loaded.Value?.Name);

[JsonSerializable(typeof(Customer))]
internal partial class CustomerJsonContext : JsonSerializerContext;

internal sealed record Customer(string Name, string Email);
```

- The package, assembly, and root namespace are all `Respire.Json`.
- With C# 14 or later, `client.Json` is an extension property on `RespireClient` and
  `IRespireClient`. It reuses one wrapper per client, does no I/O, and leaves client ownership with
  you. Key-prefixed views get their own wrapper with the view's prefix.
- On earlier C# versions, use `new RespireJsonClient(client)`.

## Paths and response shapes

| Path | Example | Returns |
| --- | --- | --- |
| Legacy (default) | `.`, `RespireJsonPath.Root` | One value |
| JSONPath | `$`, `RespireJsonPath.JsonPathRoot` | Every match |

- `GetAsync` reads one path and throws `InvalidOperationException` on more than one match. Use
  `GetManyAsync` for wildcards, or `GetJsonAsync` to read several paths as text.
- `RespireJsonGetResult<T>.Found` is false when the key is missing or a JSONPath matches nothing.
- A stored JSON `null` returns `Found = true` with a default `Value`, even for a non-nullable
  reference type.
- With a legacy path, a missing path inside an existing document is a server error. Use a `$` path
  to get `Found = false` instead.

## Reading and writing

- `GetJsonAsync` returns JSON text. `RespireJsonGetOptions` maps `Indent`, `NewLine`, `Space`, and
  `NoEscape` to `INDENT`, `NEWLINE`, `SPACE`, and `NOESCAPE`, and takes one or more `Paths`. Do not
  mix legacy and `$` paths.
- `SetAsync` serializes typed values straight to UTF-8. `SetJsonAsync` takes pre-serialized JSON as
  a string or UTF-8 bytes. Both return `false` when `RespireJsonSetCondition.Nx` or `Xx` rejects the
  write.
- `MultiGetAsync` returns one entry per key: `null` for a missing key, otherwise the matched values.
- `MultiSetAsync` writes every entry atomically with `JSON.MSET`. It serializes everything first, so
  a serialization failure writes nothing.
- `GetMemoryUsageAsync` runs `JSON.DEBUG MEMORY`.

### Floating point array storage

Redis 8.8 added `JSON.SET ... FPHA` to choose storage for floating point homogeneous arrays.
Use `RespireJsonFloatingPointType.Fp16`, `Bf16`, `Fp32`, or `Fp64` with the typed `SetAsync`
or text/UTF-8 `SetJsonAsync` overloads. The type is the first argument; existing overloads leave
storage selection to Redis. `Nx` and `Xx` still control whether the write takes place.

```csharp
using Respire;
using Respire.Json;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
bool created = await client.Json.SetJsonAsync(
    RespireJsonFloatingPointType.Fp16, "embedding:42", "[1.5,-2.5,4000.0]",
    condition: RespireJsonSetCondition.Nx);
```

The selected type trades precision for storage size. Redis converts eligible arrays, including
nested arrays, and reports a `RespireServerException` if a value exceeds the selected type's
range. Respire rejects unknown enum values before dispatch. Older servers reject the FPHA option;
use the existing overloads when targeting them. See the [Redis JSON.SET reference](https://redis.io/docs/latest/commands/json.set/)
and [Redis 8.8 changes](https://redis.io/docs/latest/develop/whats-new/8-8/).

`JSON.MSET` has no FPHA option. JSON commands currently have no typed batch facet and are not
supported by the raw batch API. Immediate `ExecuteAsync` with `RespireCommands.Json.JSON_SET`
supports the raw FPHA arguments and key prefixing; dispose its `RespireResult`.

## Low-level commands

`MergeAsync<T>(key, patch, jsonTypeInfo, path)` applies an RFC 7396 merge patch:

- Members serialized as JSON `null` are deleted. Omitting a null member leaves it unchanged, so set
  null handling deliberately.
- Arrays and scalars replace the selected value.
- A null patch replaces the target with JSON `null`; it does not delete the key.

`RespireJsonClient.Commands` exposes generated low-level methods for `JSON.GET`, `JSON.SET`,
`JSON.MGET`, `JSON.MSET`, `JSON.DEL`, `JSON.FORGET`, `JSON.CLEAR`, array, number, object, string,
type, response, and toggle commands, including `Commands.MergeAsync` (`JSON.MERGE`),
`Commands.ArrayLengthAsync` (`JSON.ARRLEN`), and `Commands.NumberPowerByAsync` (`JSON.NUMPOWBY`).

- They return Redis replies as `RespireResult`; dispose each result.
- `ArrayLengthAsync` keeps null entries for JSONPath matches that are not arrays.
- Conditional and FPHA `JSON.SET` and `JSON.DEBUG MEMORY` are available only through the typed `SetAsync`,
  `SetJsonAsync`, and `GetMemoryUsageAsync`.

### Projections

Redis 8.10 scalar projections such as `$.items.sum()`, `sum($.items)`, and `($.price + 1)` return
arrays of computed values. Typed reads unwrap that array like ordinary JSONPath matches. A
projection can yield nothing, and `GetManyAsync` then returns an empty array. See the
[Redis JSONPath reference](https://redis.io/docs/latest/develop/data-types/json/path/).

A string path (or implicit conversion) is treated as JSONPath only when it starts with `$`. For
other cases, choose the response shape explicitly:

| Factory | Decodes as | Use for |
| --- | --- | --- |
| `RespireJsonPath.Legacy(path)` | One value | Legacy paths |
| `RespireJsonPath.JsonPath(path)` | Array of matches | JSONPath |
| `RespireJsonPath.Projection(path)` | Array of matches | Projections not starting with `$`, such as `sum($.items)` or `($.price + 1)` |
| `RespireJsonPath.DirectArray(path)` | One array value | Collection projections such as `$.obj.keys()` or `$.items.append(9)` |

- None of these rewrites or validates the expression. Pick the shape Redis actually returns; a
  mismatch can fail deserialization or regroup results. The JSON reply alone cannot tell you, since
  a legacy value can itself be an array.
- `ResponseShape` exposes the stored choice. Two paths are equal only when text and shape match.
- Parenthesize numeric-leading arithmetic, for example `(2 * $.n)`. Redis reads `2 * $.n` as a
  legacy path rooted at field `2`, which may match nothing.

Collection projections return a direct JSON array with no array-of-matches wrapper. Use
`DirectArray` with array metadata to read it as one value, even though the expression starts with
`$`:

<!-- doc-test-tail-declaration: split-before=[JsonSerializable -->
```csharp
using System.Text.Json.Serialization;
using Respire;
using Respire.Json;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var keys = await client.Json.GetAsync("doc", ProjectionJsonContext.Default.StringArray,
    RespireJsonPath.DirectArray("$.obj.keys()"));

[JsonSerializable(typeof(string[]))]
internal partial class ProjectionJsonContext : JsonSerializerContext;
```

`GetManyAsync` with the same metadata and path returns one value holding that array. `Projection(...)`
or a plain string path would treat its elements as separate matches.

## Key prefixes, Cluster, and client-side caching

- Known RedisJSON key arguments get the `WithKeyPrefix` prefix.
- All keys in `JSON.MGET` and `JSON.MSET` must share a Cluster hash slot. Respire checks this before
  sending and fails with a `CROSSSLOT` error.
- RESP2 and RESP3 are both supported.
- With client-side caching, `JSON.GET`, `JSON.MGET`, and other JSON reads can be served locally.
  Single-key writes invalidate only their key; `JSON.MSET` invalidates each key it writes.
