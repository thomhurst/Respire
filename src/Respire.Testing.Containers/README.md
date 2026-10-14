# Respire.Testing.Containers

Disposable Redis and Valkey container fixtures for standalone, Cluster, and Sentinel integration tests.

## Install

```sh
dotnet add package Respire.Testing.Containers
```

## Usage

```csharp
using Respire;
using Respire.Testing.Containers;

await using var fixture = await RespireContainerFixture.StartAsync();
await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
await client.SetAsync("greeting", "hello");
Console.WriteLine(await client.GetStringAsync("greeting"));
```

Requires a local Docker engine running Linux containers. Keep this Testcontainers-based package in test projects; it does not support Native AOT. Dispose clients before the fixture.

[Read the guide](https://thomhurst.github.io/Respire/docs/guides/testing-containers) for configuration, requirements, and further examples.
