using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using VectorData.ConformanceTests;
using VectorData.ConformanceTests.Support;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

[Collection(RedisConformanceCollection.Name)]
public sealed class EmbeddingErrorTests(RedisServerFixture server)
{
    [Theory]
    [InlineData(RespireProtocol.Resp2, false)]
    [InlineData(RespireProtocol.Resp3, false)]
    [InlineData(RespireProtocol.Resp2, true)]
    [InlineData(RespireProtocol.Resp3, true)]
    public async Task SearchAsync_string_without_generator_throws(RespireProtocol protocol, bool json)
    {
        var fixture = new NoGeneratorFixture(server, protocol, json);
        await fixture.InitializeAsync();
        try
        {
            // Invoke the applicable official fact and its exact assertions. Other facts require
            // unsupported embedding generation, so do not create a discoverable suite for them.
            await new OfficialErrorContract(fixture).SearchAsync_string_without_generator_throws();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Trait("Unsupported", "FixtureAdapter")]
    private sealed class OfficialErrorContract(NoGeneratorFixture fixture) : EmbeddingGenerationTests<string>(fixture, null!);

    private sealed class NoGeneratorFixture(RedisServerFixture server, RespireProtocol protocol, bool json) : EmbeddingGenerationTests<string>.StringVectorFixture
    {
        public override TestStore TestStore { get; } = json ? new JsonTestStore(server, protocol) : new HashTestStore(server, protocol);

        // This fact requires only a raw FLOAT32 record and does not seed generated embeddings.
        public override ValueTask InitializeAsync() => new(TestStore.ReferenceCountingStartAsync());
        public override VectorStoreCollection<string, TRecord> GetCollection<TRecord>(VectorStore vectorStore, string collectionName, VectorStoreCollectionDefinition? recordDefinition = null)
        {
            if (typeof(TRecord) != typeof(EmbeddingGenerationTests<string>.RawRecord))
                throw new NotSupportedException("Only the official no-generator raw record is mapped.");
            return TestStore.CreateCollection<string, TRecord>(collectionName, new()
            {
                Properties = [new VectorStoreKeyProperty("Key", typeof(string)), new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), 3)],
            });
        }

        public override VectorStore CreateVectorStore(IEmbeddingGenerator? embeddingGenerator = null)
            => throw new NotSupportedException("Embedding generators are unsupported.");
        public override Func<IServiceCollection, IServiceCollection>[] DependencyInjectionStoreRegistrationDelegates => [];
        public override Func<IServiceCollection, IServiceCollection>[] DependencyInjectionCollectionRegistrationDelegates => [];
    }
}
