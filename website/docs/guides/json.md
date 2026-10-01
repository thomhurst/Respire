---
title: RedisJSON
---

`Respire.Json` adds typed RedisJSON commands to an existing Respire client. Use Redis Stack or Redis 8 with the JSON module enabled.

```bash
dotnet add package Respire.Json
```

`RespireJsonClient` uses `System.Text.Json` metadata supplied by the caller. This avoids reflection and supports Native AOT:

<!-- doc-test-declaration: split-before=await using var client -->
```csharp
using System.Text.Json.Serialization;
using Respire.Extensions.Json;

[JsonSerializable(typeof(Customer))]
internal partial class CustomerJsonContext : JsonSerializerContext;

internal sealed record Customer(string Name, string Email);

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var json = new RespireJsonClient(client);
var key = (RespireKey)"customer:42";
var customer = new Customer("Ada", "ada@example.test");

await json.SetAsync(key, customer, CustomerJsonContext.Default.Customer);
var loaded = await json.GetAsync(key, CustomerJsonContext.Default.Customer);
if (loaded.Found)
    Console.WriteLine(loaded.Value?.Name);
```

Use `RespireJsonPath.Root` for legacy root path `.`. Use `RespireJsonPath.JsonPathRoot` or another `$` path for JSONPath. JSONPath reads return all matches; `GetAsync` requires at most one match, while `GetManyAsync` returns every match. `RespireJsonValue<T>.Found` distinguishes a missing key or path from a JSON `null` value.

`GetJsonAsync` returns formatted JSON text. `RespireJsonGetOptions` supports `INDENT`, `NEWLINE`, `SPACE`, `NOESCAPE`, and one or more paths. `SetAsync` serializes typed values; `SetJsonAsync` accepts pre-serialized JSON. Both support `NX` and `XX` through `RespireJsonSetCondition`. `MultiGetAsync` and `MultiSetAsync` provide `JSON.MGET` and `JSON.MSET` operations.

`RespireJsonClient.Commands` exposes generated low-level methods for `JSON.GET`, `JSON.SET`, `JSON.MGET`, `JSON.MSET`, `JSON.DEL`, `JSON.FORGET`, `JSON.CLEAR`, array, number, object, string, type, response, toggle, and debug-memory commands. Low-level methods expose Redis reply types as `RespireResult`; dispose each result after use.

Known RedisJSON key arguments receive the prefix configured by `WithKeyPrefix`. All keys in `JSON.MGET` and `JSON.MSET` must share a Redis Cluster hash slot; Respire validates these layouts before dispatch. RESP2 and RESP3 use Respire's shared command transport.
