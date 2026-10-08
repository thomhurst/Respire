---
title: Generated Search schemas
description: Reflection-free Search schemas for mapped hash and JSON models.
---

# Generated Search schemas

Install `Respire.Search` alongside the hash or JSON mapper package. Add `[RespireSearch]` to a
`[RespireHash]` or `[RespireJson]` model and `[RespireSearchField]` to the properties to index.
The generator emits `ModelNameSearchSchema`, with named typed fields, an index definition, key
expansion and vector validation. It uses no runtime reflection.

```csharp
using Respire;
using Respire.Json;
using Respire.Search;
using System.Text.Json.Serialization;

[RespireJson("product:{Id}")]
[RespireSearch("products-v1", Prefixes = new[] { "product:" })]
public partial record Product(string Id,
    [property: JsonPropertyName("display.name")]
    [property: RespireSearchField(RespireSearchFieldType.Text, Alias = "name")] string Name,
    [property: RespireSearchField(RespireSearchFieldType.Tag)] string Category,
    [property: RespireSearchField(RespireSearchFieldType.Numeric, Sortable = true)] decimal Price,
    [property: RespireSearchField(RespireSearchFieldType.Vector, Dimensions = 2)] float[] Embedding);
```

Create the index explicitly with
`await client.Search.CreateIndexAsync(ProductSearchSchema.IndexName, ProductSearchSchema.Definition)`.
Then write documents with `ProductJsonMapper.SetAsync(new RespireJsonClient(client), product)`.
The schema does not create indexes during model writes. Use `ProductSearchSchema.Fields.Name.Alias`
in typed queries; its storage identifier is `$["display.name"]`, including escaping for JSON names
with quotes and backslashes. Hash identifiers use the mapped CLR property names.

Only attributed properties enter the schema. A non-empty index name and at least one explicit key
prefix are required. Prefixes are independent of the model key template; make sure the prefixes
include the keys you write. `Definition with { Prefixes = ... }` supports deployment-specific prefixes.
Generated field and prefix collections are read-only.

## Types and diagnostics

`TEXT`, `TAG`, `GEO` and `GEOSHAPE` use string properties. `NUMERIC` uses `int`, `long`, `double`
or `decimal`, including nullable variants. Nullable properties retain their mapper's missing/null
behavior. Aliases default to the CLR property name and must be distinct and non-empty.
`Weight` and `NoStem` apply to text; ASCII `Separator` and `CaseSensitive` apply to tags. `Sortable`
and `NoIndex` apply to non-vector fields. Server-specific options can still be supplied through
the manual [Search schema API](search.md).

`RESP006` reports invalid mappings, field types, aliases, prefixes, vector settings and incompatible
options. Existing `RESP004`/`RESP005` mapping diagnostics still apply. Every schema needs exactly
one hash or JSON mapping; normal mapper construction and key-template rules remain in force.

## Vectors and independent connectors

Hash vector properties use `byte[]` containing the server's binary element representation.
The generated hash mapper uses binary-safe `Dictionary<string, byte[]>` fields for these models;
scalar-only models retain `Dictionary<string, string>`. Scalar properties in binary models are UTF-8.
Full and partial Redis reads preserve owned vector bytes. Hash writes snapshot binary fields before
the first await. Field TTL and generated `Track` operations also support binary models: the tracker
owns vector snapshots, compares bytes and writes only changed fields. Existing scalar trackers retain
ordinal string comparison. Hash byte length must equal `Dimensions` times element size: FLOAT32 is 4 bytes,
FLOAT64 is 8, FLOAT16/BFLOAT16 are 2, and INT8/UINT8 are 1. The mapper does not convert byte order
or reinterpret packed values. Use a representation compatible with your Redis server.

JSON vector properties use `float[]` with `VectorType = Float32` (the default), or `double[]`
with `VectorType = Float64`. They serialize as JSON numeric arrays with no reflection. Other JSON
vector representations are rejected. Dimensions must be positive; JSON values must be finite.
Model writes, full model reads and individual JSON vector operations validate dimensions.
Nullable vectors can be absent/null. Arrays cannot appear in key templates or other field types.

The public `IRespireSearchSchema<TModel>` seam supplies static `IndexName`, `Definition`, `GetKey`
and `Validate` members. A VectorStore adapter can consume any generated schema independently:

```csharp
static RespireSearchIndexDefinition GetSchema<TModel, TSchema>()
    where TSchema : IRespireSearchSchema<TModel> => TSchema.Definition;

var definition = GetSchema<Product, ProductSearchSchema>();
```

`RespireSearchVectorValidation` is also public for independent connectors. This metadata seam does
not install a VectorStore connector, perform upserts, or provide a query-result object mapper.

## Schema migration limits

Changing attributes changes generated metadata; it does not migrate an existing Redis index.
`FT.ALTER` can add fields but cannot replace an existing field's type, vector dimensions, algorithm,
or distance metric. Create a versioned replacement index, wait for indexing, verify queries, and
switch an alias with the [Search alias API](search.md). Coordinate document representation changes
with the application and rebuild; never silently reuse an incompatible index. Dropping an index
with `deleteDocuments: true` also deletes its documents.

Search schemas and vector codecs are available on .NET 8 and .NET 10. Final Native AOT sample
execution and performance acceptance remain tracked by [#895](https://github.com/thomhurst/Respire/issues/895).
