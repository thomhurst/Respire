using Microsoft.Extensions.VectorData;
using VectorData.ConformanceTests.ModelTests;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

// The pinned upstream suite has no collection cancellation facts. These are supplemental connector tests.
[Collection(RedisConformanceCollection.Name)]
public sealed class JsonCancellationTests(RedisServerFixture server)
{
    [Theory]
    [InlineData(RespProtocol.Resp2)]
    [InlineData(RespProtocol.Resp3)]
    public async Task Cancelled_operations_preserve_cancellation_and_stored_record(RespProtocol protocol)
    {
        var store = new JsonTestStore(server, protocol);
        await store.ReferenceCountingStartAsync();
        try
        {
            using var collection = store.CreateCollection<string, BasicModelTests<string>.Record>("cancelled", new()
            {
                Properties =
                [
                    new VectorStoreKeyProperty("Key", typeof(string)),
                    new VectorStoreDataProperty("Text", typeof(string)) { IsFullTextIndexed = true },
                    new VectorStoreVectorProperty("Vector", typeof(ReadOnlyMemory<float>), 3),
                ],
            });
            await collection.EnsureCollectionExistsAsync(TestContext.Current.CancellationToken);
            var record = new BasicModelTests<string>.Record { Key = "one", Text = "original", Vector = new([1, 2, 3]) };
            await collection.UpsertAsync(record, TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var token = cancellation.Token;
            Func<Task>[] operations =
            [
                () => collection.CollectionExistsAsync(token),
                () => collection.EnsureCollectionExistsAsync(token),
                () => collection.EnsureCollectionDeletedAsync(token),
                () => store.DefaultVectorStore.CollectionExistsAsync(collection.Name, token),
                () => store.DefaultVectorStore.EnsureCollectionDeletedAsync(collection.Name, token),
                () => Drain(store.DefaultVectorStore.ListCollectionNamesAsync(token)),
                () => collection.GetAsync(record.Key, cancellationToken: token),
                () => Drain(collection.GetAsync([record.Key], cancellationToken: token)),
                () => collection.UpsertAsync(record, token),
                () => collection.UpsertAsync([record], token),
                () => collection.DeleteAsync(record.Key, token),
                () => collection.DeleteAsync([record.Key], token),
                () => Drain(collection.SearchAsync(record.Vector, 1, cancellationToken: token)),
                () => Drain(((IKeywordHybridSearchable<BasicModelTests<string>.Record>)collection)
                    .HybridSearchAsync(record.Vector, ["original"], 1, cancellationToken: token)),
            ];
            foreach (var operation in operations)
            {
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
                Assert.Equal(token, exception.CancellationToken);
            }
            Assert.True(await collection.CollectionExistsAsync(TestContext.Current.CancellationToken));
            record.AssertEqual(await collection.GetAsync(record.Key, new() { IncludeVectors = true }, TestContext.Current.CancellationToken), true, true);
        }
        finally
        {
            await store.ReferenceCountingStopAsync();
        }
    }

    private static async Task Drain<T>(IAsyncEnumerable<T> results)
    {
        await foreach (var result in results) { }
    }
}
