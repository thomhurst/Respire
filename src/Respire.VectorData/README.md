# Respire.VectorData

Explicit, AOT-friendly Microsoft.Extensions.VectorData hash and JSON collections, with KNN and hybrid search.

## Install

```sh
dotnet add package Respire.VectorData
```

## Usage

```csharp
using Respire;
using Respire.VectorData;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
using var store = new RespireVectorStore(redis, "my-application");
await foreach (var collection in store.ListCollectionNamesAsync())
    Console.WriteLine(collection);
```

Requires standalone Redis Query Engine; Cluster is unsupported. To create and query typed collections, register an explicit hash or JSON mapper as shown in the guide. The store leaves the supplied client caller-owned.

[Read the guide](https://respire.tomlonghurst.dev/docs/guides/vector-data) for configuration, requirements, and further examples.
