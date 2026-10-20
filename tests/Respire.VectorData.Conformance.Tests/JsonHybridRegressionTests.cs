using VectorData.ConformanceTests;
using VectorData.ConformanceTests.Support;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

[Collection(RedisConformanceCollection.Name)]
public sealed class JsonHybridRegressionTests(RedisServerFixture server)
{
    [Theory]
    [InlineData(RespireProtocol.Resp2)]
    [InlineData(RespireProtocol.Resp3)]
    public async Task Fresh_multi_text_collections_return_every_seeded_match(RespireProtocol protocol)
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(server.ConnectionString), token);
        var prefix = "hybrid-regression:" + Guid.NewGuid().ToString("N") + ":";
        var keys = Enumerable.Range(0, 8192).Select(i => (RespireValue)(prefix + i)).ToArray();
        try
        {
            // Force FT.CREATE to scan a populated database, even though these keys do not
            // belong to either fixture. Empty databases usually finish before seeding.
            using var seeded = await client.ExecuteAsync(RespireCommands.String.MSET,
                keys.SelectMany(key => new RespireValue[] { key, "unrelated" }).ToArray(), cancellationToken: token);
            for (var iteration = 0; iteration < 10; iteration++)
            {
                token.ThrowIfCancellationRequested();
                var store = new JsonTestStore(server, protocol);
                var text = new TextFixture(store);
                var multiText = new MultiTextFixture(store);
                var initialized = new List<VectorStoreFixture>();
                try
                {
                    foreach (var fixture in new VectorStoreFixture[] { text, multiText })
                    {
                        initialized.Add(fixture);
                        await fixture.InitializeAsync();
                    }
                    foreach (var name in new[] { text.CollectionName, multiText.CollectionName })
                        Assert.Equal("0", (await store.GetIndexInfoAsync(name, token)).Properties["indexing"].Scalar);
                    // Run the pinned upstream assertions once; a failed query is never retried.
                    await new HybridTests(text, multiText).HybridSearchAsync_with_multiple_text_properties();
                }
                finally
                {
                    foreach (var fixture in initialized.AsEnumerable().Reverse()) await fixture.DisposeAsync();
                }
            }
        }
        finally
        {
            using var removed = await client.ExecuteAsync(RespireCommands.Key.DEL, keys);
        }
    }

    private sealed class TextFixture(TestStore store) : HybridSearchTests<string>.VectorAndStringFixture
    {
        public override TestStore TestStore => store;
    }

    private sealed class MultiTextFixture(TestStore store) : HybridSearchTests<string>.MultiTextFixture
    {
        public override TestStore TestStore => store;
    }

    private sealed class HybridTests(TextFixture text, MultiTextFixture multiText) : HybridSearchTests<string>(text, multiText);
}
