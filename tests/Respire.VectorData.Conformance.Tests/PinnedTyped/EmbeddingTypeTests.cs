// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Source: dotnet/extensions 02107c65bab30aad9e35b5133ed643eaa77bccd8,
// Microsoft.Extensions.VectorData.ConformanceTests/TypeTests/EmbeddingTypeTests.cs.
// Split out mandatory dynamic/generator steps only; all raw typed assertions remain unchanged.

using System.Collections;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using VectorData.ConformanceTests.Support;
using Xunit;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning disable CA2000 // Dispose objects before losing scope

namespace Respire.VectorData.Conformance.Tests.PinnedTyped;

/// <summary>
/// Tests that the various embedding types natively supported by the provider (<c>ReadOnlyMemory&lt;float&gt;</c>, <c>ReadOnlyMemory&lt;Half&gt;</c>...) work correctly.
/// </summary>
public abstract class EmbeddingTypeTests<TKey>(EmbeddingTypeTests<TKey>.Fixture fixture)
    where TKey : notnull
{
    [Fact]
    public virtual Task ReadOnlyMemory_of_float()
        => Test(
            new ReadOnlyMemory<float>([1, 2, 3]),
            new ReadOnlyMemoryEmbeddingGenerator<float>([1, 2, 3]),
            vectorEqualityAsserter: (e, a) => Assert.Equal(e.ToArray(), a.ToArray()));

    [Fact]
    public virtual Task Embedding_of_float()
        => Test(
            new Embedding<float>(new ReadOnlyMemory<float>([1, 2, 3])),
            new ReadOnlyMemoryEmbeddingGenerator<float>([1, 2, 3]),
            vectorEqualityAsserter: (e, a) => Assert.Equal(e.Vector.ToArray(), a.Vector.ToArray()));

    [Fact]
    public virtual Task Array_of_float()
        => Test<float[]>(
            [1, 2, 3],
            new ReadOnlyMemoryEmbeddingGenerator<float>([1, 2, 3]));

    protected virtual async Task Test<TVector>(
        TVector value,
        IEmbeddingGenerator? embeddingGenerator = null,
        Action<TVector, TVector>? vectorEqualityAsserter = null,
        string? distanceFunction = null,
        int dimensions = 3)
        where TVector : notnull
    {
        vectorEqualityAsserter ??= (e, a) => Assert.Equal(e, a);

        await fixture.VectorStore.EnsureCollectionDeletedAsync(fixture.CollectionName);

        var collection = fixture.CreateCollection<TKey, Record<TVector>>(fixture.CollectionName, fixture.CreateRecordDefinition<TVector>(embeddingGenerator: null, distanceFunction, dimensions));
        await collection.EnsureCollectionExistsAsync();

        var key = fixture.GenerateNextKey<TKey>();
        var record = new Record<TVector>
        {
            Key = key,
            Vector = value,
            Int = 42
        };

        await collection.UpsertAsync(record);

        await fixture.TestStore.WaitForDataAsync(collection, recordCount: 1, filter: r => r.Int == 42, dummyVector: value);

        var result1 = await collection.GetAsync(key, new() { IncludeVectors = true });
        Assert.Equal(42, result1!.Int);
        if (fixture.TestStore.VectorsComparable)
        {
            vectorEqualityAsserter(value, result1.Vector);
        }

        // Note that some databases leak indexing information across deletion/recreation of the same collection name (e.g. Pinecone) - so we also
        // filter by Int here.
        var result2 = await collection.SearchAsync(value, top: 1, new() { IncludeVectors = true, Filter = r => r.Int == 42 }).SingleAsync();
        Assert.Equal(42, result2.Record.Int);
        if (fixture.TestStore.VectorsComparable)
        {
            vectorEqualityAsserter(value, result2.Record.Vector);
        }

        // Dynamic mapping and embedding generation are excluded; typed assertions remain unchanged.
    }

    protected sealed class ReadOnlyMemoryEmbeddingGenerator<T>(T[] data) : IEmbeddingGenerator<string, Embedding<T>>
    {
        public Task<GeneratedEmbeddings<Embedding<T>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<T>>([new(data)]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    protected sealed class BinaryEmbeddingGenerator(BitArray data) : IEmbeddingGenerator<string, BinaryEmbedding>
    {
        public Task<GeneratedEmbeddings<BinaryEmbedding>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<BinaryEmbedding>([new(data)]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    public class RecordWithString
    {
        public TKey Key { get; set; }
        public string Vector { get; set; }
        public int Int { get; set; }
    }

    public class Record<TVector>
    {
        public TKey Key { get; set; }
        public TVector Vector { get; set; }
        public int Int { get; set; }
    }

    public abstract class Fixture : VectorStoreFixture
    {
        protected virtual string CollectionNameBase => nameof(EmbeddingTypeTests<>);
        public virtual string CollectionName => TestStore.AdjustCollectionName(CollectionNameBase);

        public virtual VectorStoreCollectionDefinition CreateRecordDefinition<TVectorProperty>(IEmbeddingGenerator? embeddingGenerator, string? distanceFunction, int dimensions)
            => new()
            {
                Properties =
                [
                    new VectorStoreKeyProperty("Key", typeof(TKey)),
                    new VectorStoreVectorProperty("Vector", typeof(TVectorProperty), dimensions)
                    {
                        DistanceFunction = distanceFunction ?? DefaultDistanceFunction,
                        IndexKind = DefaultIndexKind
                    },
                    new VectorStoreDataProperty("Int", typeof(int)) { IsIndexed = true }
                ],
                EmbeddingGenerator = embeddingGenerator
            };

        /// <summary>
        /// Gets a value indicating whether the collection is recreated while testing instead of deleting the records.
        /// </summary>
        /// <remarks>
        /// This is necessary for InMemory, where the .NET type mapped on the collection cannot be changed.
        /// </remarks>
        public virtual bool RecreateCollection => false;

        /// <summary>
        /// Gets a value indicating whether to assert that no vectors were loaded when embedding generation is used.
        /// </summary>
        /// <remarks>
        /// Asserting that no vectors were loaded is necessary for InMemory, which returns the same object that was inserted and therefore contains
        /// the original input value.
        /// </remarks>
        public virtual bool AssertNoVectorsLoadedWithEmbeddingGeneration => true;
    }
}
