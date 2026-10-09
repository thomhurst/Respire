// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses the original contracts under the MIT license.
// Adapted for TUnit, explicit immutable mapping and the supported hash subset from:
// https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/CollectionManagementTests.cs
// https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/ModelTests/BasicModelTests.cs
// These named contracts are a subset, not an execution of the full upstream suite.

using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Respire.IntegrationTests;
using Respire.Samples.VectorData;
using TUnit.Core;

namespace Respire.VectorData.Tests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class UpstreamContractTests(ModernRedisTestContainer fixture)
{
    [Test, Arguments(2), Arguments(3)]
    public Task Collection_Ensure_Exists_Delete(int protocol) => WithCollection(protocol, async (store, collection) =>
    {
        (await collection.CollectionExistsAsync()).Should().BeFalse();
        await collection.EnsureCollectionExistsAsync();
        (await collection.CollectionExistsAsync()).Should().BeTrue();
        await collection.EnsureCollectionDeletedAsync();
        (await collection.CollectionExistsAsync()).Should().BeFalse();
        await store.EnsureCollectionDeletedAsync(collection.Name);
    });

    [Test, Arguments(2), Arguments(3)]
    public Task EnsureCollectionExists_twice_does_not_throw(int protocol) => WithCollection(protocol, async (_, collection) =>
    {
        await collection.EnsureCollectionExistsAsync();
        await collection.EnsureCollectionExistsAsync();
        (await collection.CollectionExistsAsync()).Should().BeTrue();
    });

    [Test, Arguments(2), Arguments(3)]
    public Task Store_CollectionExists(int protocol) => WithCollection(protocol, async (store, collection) =>
    {
        (await store.CollectionExistsAsync(collection.Name)).Should().BeFalse();
        await collection.EnsureCollectionExistsAsync();
        (await store.CollectionExistsAsync(collection.Name)).Should().BeTrue();
    });

    [Test, Arguments(2), Arguments(3)]
    public Task Store_DeleteCollection(int protocol) => WithCollection(protocol, async (store, collection) =>
    {
        await collection.EnsureCollectionExistsAsync();
        await store.EnsureCollectionDeletedAsync(collection.Name);
        (await collection.CollectionExistsAsync()).Should().BeFalse();
    });

    [Test, Arguments(2), Arguments(3)]
    public Task Store_ListCollections(int protocol) => WithCollection(protocol, async (store, collection) =>
    {
        (await Collect(store.ListCollectionNamesAsync())).Should().BeEmpty();
        await collection.EnsureCollectionExistsAsync();
        (await Collect(store.ListCollectionNamesAsync())).Should().ContainSingle().Which.Should().Be(collection.Name);
    });

    [Test, Arguments(2), Arguments(3)]
    public Task Collection_metadata(int protocol) => WithCollection(protocol, (_, collection) =>
    {
        var metadata = (VectorStoreCollectionMetadata?)collection.GetService(typeof(VectorStoreCollectionMetadata));
        metadata.Should().NotBeNull();
        metadata!.VectorStoreSystemName.Should().NotBeNull();
        metadata.CollectionName.Should().NotBeNull();
        return Task.CompletedTask;
    });

    [Test, Arguments(2, false), Arguments(2, true), Arguments(3, false), Arguments(3, true)]
    public Task GetAsync_single_record(int protocol, bool includeVectors) => WithCollection(protocol, async (_, collection) =>
    {
        var expected = new Movie("one", "First", new float[] { 1, 0 });
        await collection.UpsertAsync(expected);
        var received = await collection.GetAsync(expected.Id, new() { IncludeVectors = includeVectors });
        AssertRecord(expected, received, includeVectors);
    });

    [Test, Arguments(2, false), Arguments(2, true), Arguments(3, false), Arguments(3, true)]
    public Task GetAsync_multiple_records(int protocol, bool includeVectors) => WithCollection(protocol, async (_, collection) =>
    {
        Movie[] expected = [new("one", "First", new float[] { 1, 0 }), new("two", "Second", new float[] { 0, 1 })];
        await collection.UpsertAsync(expected);
        var received = await Collect(collection.GetAsync(expected.Select(record => record.Id), new() { IncludeVectors = includeVectors }));
        received.Should().HaveCount(2);
        foreach (var record in expected) AssertRecord(record, received.Single(value => value.Id == record.Id), includeVectors);
    });

    [Test, Arguments(2), Arguments(3)]
    public Task Insert_and_update_in_same_batch(int protocol) => WithCollection(protocol, async (_, collection) =>
    {
        await collection.UpsertAsync(new Movie("existing", "Old", new float[] { 1, 0 }));
        Movie[] records = [new("new", "New record", new float[] { 1, 1 }), new("existing", "Updated record", new float[] { 0, 1 })];
        await collection.UpsertAsync(records);
        var received = await Collect(collection.GetAsync(records.Select(record => record.Id), new() { IncludeVectors = true }));
        received.Should().HaveCount(2);
        foreach (var record in records) AssertRecord(record, received.Single(value => value.Id == record.Id), includeVectors: true);
    });

    private async Task WithCollection(int protocol, Func<RespireVectorStore, RespireVectorStoreCollection<Movie>, Task> test)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        using var store = new RespireVectorStore(client, "conformance:" + Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new MovieMapper());
        using var collection = store.GetHashCollection<Movie>("movies");
        try { await test(store, collection); }
        finally
        {
            // BasicModelTests permits CRUD without creating an index. Create it to include those hashes in DD cleanup.
            await collection.EnsureCollectionExistsAsync();
            await collection.EnsureCollectionDeletedAsync();
        }
    }

    private static void AssertRecord(Movie expected, Movie? received, bool includeVectors)
    {
        received.Should().NotBeNull();
        received!.Id.Should().Be(expected.Id);
        received.Title.Should().Be(expected.Title);
        received.Tag.Should().Be(expected.Tag);
        if (includeVectors) received.Vector.ToArray().Should().Equal(expected.Vector.ToArray());
        else received.Vector.IsEmpty.Should().BeTrue();
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();
        await foreach (var value in values) result.Add(value);
        return result;
    }
}
