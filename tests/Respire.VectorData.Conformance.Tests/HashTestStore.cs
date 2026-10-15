using Microsoft.Extensions.VectorData;
using VectorData.ConformanceTests.Support;

namespace Respire.VectorData.Conformance.Tests;

// The upstream definitions configure test-only mappers, never production reflection mapping.
internal sealed class HashTestStore(RedisServerFixture server, RespireProtocol protocol) : TestStore
{
    private readonly string _namespace = "official-hash:" + Guid.NewGuid().ToString("N");
    private readonly List<RespireVectorStore> _stores = [];
    private readonly List<IDisposable> _handles = [];
    private readonly HashSet<string> _collections = [];
    private RespireClient? _client;

    public override string DefaultDistanceFunction => DistanceFunction.CosineDistance;

    protected override async Task StartAsync()
    {
        _client = await RespireClient.ConnectAsync(RespireOptions.Parse(server.ConnectionString) with { Protocol = protocol }).ConfigureAwait(false);
        DefaultVectorStore = new RespireVectorStore(_client, _namespace);
    }

    public override VectorStoreCollection<TKey, TRecord> CreateCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition definition)
    {
        if (typeof(TKey) != typeof(string)) throw new NotSupportedException("The connector supports string keys only.");
        // A fresh mapper registration lets distance/index tests recreate the same record with a new schema.
        var store = new RespireVectorStore(_client!, _namespace);
        _stores.Add(store);
        store.RegisterMapper(new HashDefinitionMapper<TRecord>(definition));
        _collections.Add(name);
        var collection = store.GetHashCollection<TRecord>(name);
        _handles.Add(collection);
        return (VectorStoreCollection<TKey, TRecord>)(object)collection;
    }

    protected override async Task StopAsync()
    {
        if (_client is not { } client) return;
        _client = null;
        try
        {
            // Include collections whose tests wrote records without creating an index.
            foreach (var name in _collections)
                await DefaultVectorStore.EnsureCollectionDeletedAsync(name).ConfigureAwait(false);
        }
        finally
        {
            foreach (var collection in _handles) collection.Dispose();
            _handles.Clear();
            foreach (var store in _stores) store.Dispose();
            _stores.Clear();
            _collections.Clear();
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
