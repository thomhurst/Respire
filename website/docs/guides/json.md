---
title: Redis JSON
---

`Respire.Json` provides typed Redis JSON commands. JSON is built into Redis Open Source 8 and later; older deployments need Redis Stack or the RedisJSON module. The package, assembly, and root namespace are all `Respire.Json`.

```bash
dotnet add package Respire.Json
```

With C# 14 or later, import the namespace shown below and use `client.Json` on
`RespireClient` or `IRespireClient`. The property reuses one wrapper per client instance,
performs no network I/O, and leaves ownership of the underlying client with you.
Key-prefixed views get their own wrapper and retain the module's prefix restrictions.
The existing `new RespireJsonClient(client)` constructor remains available.

`RespireJsonClient` uses `System.Text.Json` metadata supplied by the caller. This avoids reflection and supports Native AOT:

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

## Paths and response shapes

The default `RespireJsonPath` is the legacy root path `.` (also `RespireJsonPath.Root`); legacy paths return one value. Use `RespireJsonPath.JsonPathRoot` or another `$` path for JSONPath, which returns every match. `GetAsync` requires at most one match and throws `InvalidOperationException` otherwise, so use `GetManyAsync` for wildcard paths. `GetAsync` reads one path; read several paths at once as text with `GetJsonAsync`.

`RespireJsonValue<T>.Found` is false when the key is missing or a JSONPath matches nothing. A stored JSON `null` returns `Found = true` with a default `Value`, even when `T` is a non-nullable reference type. With a legacy path, a missing path inside an existing document is a server error; use a `$` path to get `Found = false` instead.

## Reading and writing

`GetJsonAsync` returns formatted JSON text. `RespireJsonGetOptions` supports `INDENT`, `NEWLINE`, `SPACE`, `NOESCAPE`, and one or more paths; do not mix legacy and `$` paths. `SetAsync` serializes typed values straight to UTF-8. `SetJsonAsync` accepts pre-serialized JSON as a string or as UTF-8 bytes. All three return `false` when `RespireJsonSetCondition.Nx` or `Xx` rejects the write.

`MultiGetAsync` returns one entry per key: `null` when the key does not exist, otherwise the values matched by the path. `MultiSetAsync` writes every entry atomically with `JSON.MSET`; it serializes all entries before sending, so a serialization failure writes nothing. `GetMemoryUsageAsync` runs `JSON.DEBUG MEMORY`.

## Low-level commands

`MergeAsync<T>(key, patch, jsonTypeInfo, path)` applies an RFC 7396 merge patch using the
supplied `System.Text.Json` metadata. Object members serialized as JSON `null` are deleted;
arrays and scalar values replace the selected value. Configure null handling deliberately:
omitting a null member from the serialized patch leaves that member unchanged.
A null patch itself replaces the target with JSON `null`; it does not delete the document key.

The generated `Commands.MergeAsync`, `Commands.ArrayLengthAsync`, and
`Commands.NumberPowerByAsync` expose `JSON.MERGE`, `JSON.ARRLEN`, and `JSON.NUMPOWBY`.
`ArrayLengthAsync` preserves null entries for JSONPath matches that are not arrays.

Redis 8.10 scalar projection expressions, including `$.items.sum()`, `sum($.items)`, and
`($.price + 1)`, return arrays of computed values. Typed reads unwrap that outer array using the
supplied result metadata, just like ordinary JSONPath matches. A projection can yield no
value; `GetManyAsync` then returns an empty array. See the
[Redis JSONPath reference](https://redis.io/docs/latest/develop/data-types/json/path/).
The string constructor retains its original rule: only paths starting with `$` select an
array-of-matches reply. For other expressions, use `RespireJsonPath.Projection("sum($.items)")`
or `RespireJsonPath.Projection("($.price + 1)")`. `Legacy(path)` explicitly selects one JSON
value; `JsonPath(path)` and `Projection(path)` explicitly select matched values. None rewrites
the text sent to Redis or validates that its syntax agrees with the selected response shape.
Choose the shape Redis actually returns. A mismatched override can fail deserialization
or change how results are grouped; the direct-array example below uses a matching override.
`ResponseShape` exposes the stored choice without parsing the expression.
Group numeric-leading arithmetic, for example `(2 * $.n)`: Redis interprets the ungrouped
`2 * $.n` as a legacy expression rooted at the field `2`, which may produce no match.
Respire does not parse the complete Redis expression grammar. A projection path compares
equal to another path only when both its text and response shape match.
Inspecting the JSON reply alone cannot infer the choice: a legacy value may itself be an array.

Collection-valued projections such as `$.obj.keys()` and `$.items.append(9)` return a direct
JSON array, without an extra array-of-matches wrapper. Pass `RespireJsonPath.DirectArray(expression)`
and array metadata to preserve it as one typed value, even though these expressions start with `$`:

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

`GetManyAsync` with the same metadata and path returns one value containing that array.
`DirectArray` names this intent explicitly; it selects the same single-value decoding as
`Legacy` and preserves the expression unchanged. The caller supplies matching array metadata.
Using `Projection(...)` or an implicit string path would instead interpret its elements as separate matches.

`RespireJsonClient.Commands` exposes generated low-level methods for `JSON.GET`, `JSON.SET`, `JSON.MGET`, `JSON.MSET`, `JSON.DEL`, `JSON.FORGET`, `JSON.CLEAR`, array, number, object, string, type, response, and toggle commands. Low-level methods expose Redis reply types as `RespireResult`; dispose each result after use. Conditional `JSON.SET` and `JSON.DEBUG MEMORY` take fixed modifier tokens, so they are available only through the typed `SetAsync`, `SetJsonAsync`, and `GetMemoryUsageAsync` methods.

## Key prefixes, Cluster, and client-side caching

Known RedisJSON key arguments receive the prefix configured by `WithKeyPrefix`. All keys in `JSON.MGET` and `JSON.MSET` must share a Redis Cluster hash slot; Respire validates these layouts before dispatch and fails with a `CROSSSLOT` error without sending the command. RESP2 and RESP3 use Respire's shared command transport.

With client-side caching enabled, JSON reads such as `JSON.GET` and `JSON.MGET` can be served from the local cache. Single-key JSON writes invalidate only their document key. `JSON.MSET` invalidates each document key it writes, as core `MSET` does, and leaves other cached entries in place.
