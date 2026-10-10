using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.VectorData;
using Respire.Search;
using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;
using VectorData.ConformanceTests.Support;

namespace Respire.VectorData.Conformance.Tests;

internal sealed class JsonTestStore(RedisServerFixture server, RespProtocol protocol) : TestStore
{
    private RespireClient? _client;

    public override string DefaultDistanceFunction => DistanceFunction.CosineDistance;

    protected override async Task StartAsync()
    {
        _client = await RespireClient.ConnectAsync(RespireOptions.Parse(server.ConnectionString) with { Protocol = protocol }).ConfigureAwait(false);
        DefaultVectorStore = new RespireVectorStore(_client, "official-json:" + Guid.NewGuid().ToString("N"));
    }

    protected override async Task StopAsync()
    {
        if (_client is not { } client) return;
        _client = null;
        try
        {
            await foreach (var name in DefaultVectorStore.ListCollectionNamesAsync().ConfigureAwait(false))
                await DefaultVectorStore.EnsureCollectionDeletedAsync(name).ConfigureAwait(false);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override VectorStoreCollection<TKey, TRecord> CreateCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition definition)
    {
        if (typeof(TKey) != typeof(string)) throw new NotSupportedException("JSON collections require string keys.");
        // GetTypeInfo consults only this source-generated context; reflection serialization is disabled.
        var metadata = (JsonTypeInfo<TRecord>)(JsonModelsContext.Default.GetTypeInfo(typeof(TRecord))
            ?? throw new NotSupportedException("The upstream model requires explicit generated JSON metadata."));
        var store = (RespireVectorStore)DefaultVectorStore;
        store.RegisterMapper(new JsonConformanceMapper<TRecord>(metadata, definition));
        return (VectorStoreCollection<TKey, TRecord>)(object)store.GetJsonCollection<TRecord>(name);
    }
}

internal sealed class JsonConformanceMapper<TRecord> : RespireVectorDataJsonMapper<TRecord> where TRecord : class
{
    private readonly Func<object, object?> _key;

    public JsonConformanceMapper(JsonTypeInfo<TRecord> metadata, VectorStoreCollectionDefinition definition) : base(metadata)
    {
        var key = definition.Properties.OfType<VectorStoreKeyProperty>().Single();
        _key = metadata.Properties.Single(p => p.Name == key.Name).Get!;
        VectorFields = definition.Properties.OfType<VectorStoreVectorProperty>().Select(vector =>
            new RespireVectorDataVectorField(vector.Name, vector.Name, vector.Dimensions)
            {
                Algorithm = vector.IndexKind switch
                {
                    null or IndexKind.Flat => RespireSearchVectorAlgorithm.Flat,
                    IndexKind.Hnsw => RespireSearchVectorAlgorithm.Hnsw,
                    _ => throw new NotSupportedException("Unsupported index kind."),
                },
                DistanceMetric = vector.DistanceFunction switch
                {
                    null or DistanceFunction.CosineDistance => RespireSearchDistanceMetric.Cosine,
                    DistanceFunction.NegativeDotProductSimilarity => RespireSearchDistanceMetric.InnerProduct,
                    DistanceFunction.EuclideanSquaredDistance => RespireSearchDistanceMetric.L2,
                    _ => throw new NotSupportedException("Unsupported distance function."),
                },
            }).ToArray();
        var data = definition.Properties.OfType<VectorStoreDataProperty>().ToArray();
        DataFields = data.Select(property => new RespireSearchField("$." + property.Name,
            property.Type == typeof(string) ? RespireSearchFieldType.Text : RespireSearchFieldType.Numeric,
            Alias: property.Name)).ToArray();
        TextFields = data.Where(property => property.IsFullTextIndexed)
            .Select(property => new RespireVectorDataTextField(property.Name, property.Name)).ToArray();
    }

    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; }
    public override IReadOnlyList<RespireSearchField> DataFields { get; }
    public override IReadOnlyList<RespireVectorDataTextField> TextFields { get; }
    public override string GetKey(TRecord record) => (string)_key(record)!;
}

[JsonSerializable(typeof(BasicModelTests<string>.Record))]
[JsonSerializable(typeof(NoDataModelTests<string>.NoDataRecord))]
[JsonSerializable(typeof(MultiVectorModelTests<string>.MultiVectorRecord))]
[JsonSerializable(typeof(DistanceFunctionTests<string>.SearchRecord), TypeInfoPropertyName = "DistanceRecord")]
[JsonSerializable(typeof(IndexKindTests<string>.SearchRecord), TypeInfoPropertyName = "IndexRecord")]
[JsonSerializable(typeof(HybridSearchTests<string>.VectorAndStringRecord<string>))]
[JsonSerializable(typeof(HybridSearchTests<string>.MultiTextStringRecord<string>))]
internal partial class JsonModelsContext : JsonSerializerContext;
