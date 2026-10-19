# Respire.Json

Typed Redis JSON document operations with explicit System.Text.Json metadata and raw JSON overloads.

## Install

```sh
dotnet add package Respire.Json
```

## Usage

```csharp
using Respire;
using Respire.Json;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var json = new RespireJsonClient(redis);
await json.SetJsonAsync("customer:42", """{"name":"Ada"}""");
```

Requires Redis JSON support on the server. See the guide for typed reads and writes using source-generated JSON metadata.

[Read the guide](https://respire.tomlonghurst.dev/docs/guides/json) for configuration, requirements, and further examples.
