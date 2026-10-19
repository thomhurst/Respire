# Respire

A multiplexed .NET client for Redis, Valkey, KeyDB, and compatible RESP servers.

## Install

```sh
dotnet add package Respire
```

## Usage

```csharp
using Respire;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
await redis.SetAsync("greeting", "hello", expiry: TimeSpan.FromMinutes(5));
Console.WriteLine(await redis.GetStringAsync("greeting"));
```

Requires .NET 8 or .NET 10 and a running RESP server. Share one client across callers and dispose it at shutdown.

[Read the guide](https://respire.tomlonghurst.dev/docs/getting-started) for configuration, requirements, and further examples.
