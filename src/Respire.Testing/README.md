# Respire.Testing

A deterministic in-memory RESP server for testing real Respire client code without Docker or sockets.

## Install

```sh
dotnet add package Respire.Testing
```

## Usage

```csharp
using Respire;
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await client.SetAsync("greeting", "hello");
Console.WriteLine(await client.GetStringAsync("greeting"));
```

Keep this package and Respire at the same version. The fake supports a documented subset of commands; use a real server for compatibility tests.

[Read the guide](https://respire.tomlonghurst.dev/docs/guides/in-memory-testing) for configuration, requirements, and further examples.
