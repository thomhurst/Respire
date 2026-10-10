using Microsoft.Extensions.VectorData;
using VectorData.ConformanceTests.Support;
using Record = VectorData.ConformanceTests.CollectionManagementTests<string>.Record;

namespace Respire.VectorData.Conformance.Tests;

internal sealed class LifecycleTestStore(RedisServerFixture server, RespProtocol protocol, bool json) : TestStore
{
    private RespireClient? _client;

    protected override async Task StartAsync()
    {
        _client = await RespireClient.ConnectAsync(RespireOptions.Parse(server.ConnectionString) with { Protocol = protocol }).ConfigureAwait(false);
        try
        {
            var store = new RespireVectorStore(_client, "official-lifecycle:" + Guid.NewGuid().ToString("N"));
            store.RegisterMapper<Record>(json ? new LifecycleJsonMapper() : new LifecycleHashMapper());
            DefaultVectorStore = store;
        }
        catch
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            throw;
        }
    }

    protected override async Task StopAsync()
    {
        // Upstream owns store disposal; this fixture owns only its client.
        if (_client is { } client)
        {
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
    }

    public override VectorStoreCollection<TKey, TRecord> CreateCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition definition)
    {
        if (typeof(TKey) != typeof(string) || typeof(TRecord) != typeof(Record))
            throw new NotSupportedException("This fixture maps the official string-keyed lifecycle record only.");
        var store = (RespireVectorStore)DefaultVectorStore;
        var collection = json ? store.GetJsonCollection<Record>(name) : store.GetHashCollection<Record>(name);
        return (VectorStoreCollection<TKey, TRecord>)(object)collection;
    }
}
