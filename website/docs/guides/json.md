---
title: RedisJSON
---

`Respire.Json` adds typed RedisJSON commands to an existing Respire client. Use Redis Stack or Redis 8 with the JSON module enabled.

```bash
dotnet add package Respire.Json
```

With C# 14 or later, import the namespace shown below and use `client.Json` on
`RespireClient` or `IRespireClient`. The property reuses one wrapper per client instance,
performs no network I/O, and leaves ownership of the underlying client with you.
Key-prefixed views get their own wrapper and retain the module's prefix restrictions.
The existing `new RespireJsonClient(client)` constructor remains available.

`RespireJsonClient` uses `System.Text.Json` metadata supplied by the caller. This avoids reflection and supports Native AOT:

<!-- doc-test-declaration: split-before=await using var client -->
```csharp
using System.Text.Json.Serialization;
using Respire.Extensions.Json;

[JsonSerializable(typeof(Customer))]
internal partial class CustomerJsonContext : JsonSerializerContext;

internal sealed record Customer(string Name, string Email);

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var json = client.Json;
var key = (RespireKey)"customer:42";
var customer = new Customer("Ada", "ada@example.test");

await json.SetAsync(key, customer, CustomerJsonContext.Default.Customer);
var loaded = await json.GetAsync(key, CustomerJsonContext.Default.Customer);
if (loaded.Found)
    Console.WriteLine(loaded.Value?.Name);
```

## Paths and response shapes

The default `RespireJsonPath` is the legacy root path `.` (also `RespireJsonPath.Root`); legacy paths return one value. Use `RespireJsonPath.JsonPathRoot` or another `$` path for JSONPath, which returns every match. `GetAsync` requires at most one match and throws `InvalidOperationException` otherwise, so use `GetManyAsync` for wildcard paths. `GetAsync` reads one path; read several paths at once as text with `GetJsonAsync`.

`RespireJsonValue<T>.Found` is false when the key is missing or a JSONPath matches nothing. A stored JSON `null` returns `Found = true` with a default `Value`, even when `T` is a non-nullable reference type. With a legacy path, a missing path inside an existing document is a server error; use a `$` path to get `Found = false` instead.

## Reading and writing

`GetJsonAsync` returns formatted JSON text. `RespireJsonGetOptions` supports `INDENT`, `NEWLINE`, `SPACE`, `NOESCAPE`, and one or more paths; do not mix legacy and `$` paths. `SetAsync` serializes typed values straight to UTF-8. `SetJsonAsync` accepts pre-serialized JSON as a string or as UTF-8 bytes. All three return `false` when `RespireJsonSetCondition.Nx` or `Xx` rejects the write.

`MultiGetAsync` returns one entry per key: `null` when the key does not exist, otherwise the values matched by the path. `MultiSetAsync` writes every entry atomically with `JSON.MSET`; it serializes all entries before sending, so a serialization failure writes nothing. `GetMemoryUsageAsync` runs `JSON.DEBUG MEMORY`.

## Low-level commands

`RespireJsonClient.Commands` exposes generated low-level methods for `JSON.GET`, `JSON.SET`, `JSON.MGET`, `JSON.MSET`, `JSON.DEL`, `JSON.FORGET`, `JSON.CLEAR`, array, number, object, string, type, response, and toggle commands. Low-level methods expose Redis reply types as `RespireResult`; dispose each result after use. Conditional `JSON.SET` and `JSON.DEBUG MEMORY` take fixed modifier tokens, so they are available only through the typed `SetAsync`, `SetJsonAsync`, and `GetMemoryUsageAsync` methods.

## Key prefixes, Cluster, and client-side caching

Known RedisJSON key arguments receive the prefix configured by `WithKeyPrefix`. All keys in `JSON.MGET` and `JSON.MSET` must share a Redis Cluster hash slot; Respire validates these layouts before dispatch and fails with a `CROSSSLOT` error without sending the command. RESP2 and RESP3 use Respire's shared command transport.

With client-side caching enabled, JSON reads such as `JSON.GET` and `JSON.MGET` can be served from the local cache. Single-key JSON writes invalidate only their document key. `JSON.MSET` invalidates each document key it writes, as core `MSET` does, and leaves other cached entries in place.
