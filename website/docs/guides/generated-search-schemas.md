---
title: Generated Search schemas
description: Reflection-free Search schemas for mapped hash and JSON models.
---

# Generated Search schemas

Generate a Search index definition from a mapped hash or JSON model instead of writing it by hand. Install `Respire.Search` alongside the hash or JSON mapper package. Add `[RespireSearch]` to a `[RespireHash]` or `[RespireJson]` model and `[RespireSearchField]` to each property to index. The generator emits `ModelNameSearchSchema` with typed fields, an index definition, key expansion and vector validation, without runtime reflection.

<!-- doc-test-top-level-tail-declaration: split-before=[RespireJson -->
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

1. Create the index: `await client.Search.CreateIndexAsync(ProductSearchSchema.IndexName, ProductSearchSchema.Definition)`. Model writes never create it for you.
2. Write documents: `await ProductJsonMapper.SetAsync(client.Json, product)`.
3. Query with `ProductSearchSchema.Fields.Name.Alias`. Its JSON storage identifier is `$["display.name"]`, with quotes and backslashes escaped. Hash identifiers use the CLR property names.

Rules:

- Only attributed properties enter the schema.
- You need a non-empty index name, at least one indexed property and at least one key prefix.
- Prefixes are independent of the key template; make sure they cover the keys you write. Use `Definition with { Prefixes = ... }` for deployment-specific prefixes.
- Generated field and prefix collections are read-only.

## Types and diagnostics

| Field type | Property types |
| --- | --- |
| `TEXT`, `TAG`, `GEO`, `GEOSHAPE` | `string` |
| `NUMERIC` | `int`, `long`, `double`, `decimal` and nullable forms |
| `VECTOR` | See [Vectors](#vectors-and-independent-connectors) |

- Nullable properties keep their mapper's missing/null behavior.
- Aliases default to the CLR property name and must be distinct and non-empty.
- `Weight` and `NoStem` apply to text; ASCII `Separator` and `CaseSensitive` to tags; `Sortable` and `NoIndex` to non-vector fields.
- For server-specific options, use the manual [Search schema API](search.md).

`RESP006` reports invalid mappings, field types, aliases, prefixes, vector settings and incompatible options. `RESP004`/`RESP005` mapping diagnostics still apply. Each schema needs exactly one hash or JSON mapping.

## Vectors and independent connectors

**Hash vectors** use `byte[]` in the server's binary element format.

- Byte length must equal `Dimensions` times element size: FLOAT32 4, FLOAT64 8, FLOAT16/BFLOAT16 2, INT8/UINT8 1.
- The mapper does not convert byte order or reinterpret values; write a format your Redis server accepts.
- Models with vectors use binary `Dictionary<string, byte[]>` fields, with scalars as UTF-8. Scalar-only models keep `Dictionary<string, string>`.
- Reads return owned vector bytes. Writes snapshot binary fields before the first await.
- Field TTL and generated `Track` operations support binary models; the tracker compares bytes and writes only changed fields.

**JSON vectors** use `float[]` with `VectorType = Float32` (default) or `double[]` with `VectorType = Float64`, stored as numeric arrays. Other representations are rejected.

Both: dimensions must be positive and JSON values finite. Model writes, full reads and JSON vector operations validate dimensions. Nullable vectors can be null or absent. Arrays cannot appear in key templates or other field types.

`IRespireSearchSchema<TModel>` exposes static `IndexName`, `Definition`, `GetKey` and `Validate`, so a VectorStore adapter can consume any generated schema:

<!-- doc-test-top-level-tail-declaration: split-before=[RespireJson -->
```csharp
using Respire.Json;
using Respire.Search;

static RespireSearchIndexDefinition GetSchema<TModel, TSchema>()
    where TSchema : IRespireSearchSchema<TModel> => TSchema.Definition;

var definition = GetSchema<Product, ProductSearchSchema>();

[RespireJson("product:{Id}")]
[RespireSearch("products-v1", Prefixes = new[] { "product:" })]
public partial record Product(string Id,
    [property: RespireSearchField(RespireSearchFieldType.Text)] string Name);
```

`RespireSearchVectorValidation` is also public. This metadata does not provide a VectorStore connector, upserts or a query-result mapper.

**`RespireHashModelIO.WriteBinaryAsync` does not check vector dimensions.** Use the generated mapper, or validate vector bytes before calling it.

## Schema migration limits

Changing attributes changes generated metadata, not an existing Redis index. `FT.ALTER` can add fields but cannot change a field's type, vector dimensions, algorithm or distance metric. To change these:

1. Create a versioned replacement index.
2. Wait for indexing and verify queries.
3. Switch an alias with the [Search alias API](search.md).

Coordinate document format changes with the application rebuild; never reuse an incompatible index. **Dropping an index with `deleteDocuments: true` also deletes its documents.**

Available on .NET 8 and .NET 10.
