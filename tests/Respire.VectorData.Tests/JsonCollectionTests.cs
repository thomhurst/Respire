// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses the original contracts under the MIT license.
// Named CRUD contracts adapted for TUnit and explicit JSON mappings from:
// https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/ModelTests/BasicModelTests.cs
// Multi-vector inclusion/selection contracts adapted from MultiVectorModelTests.cs at the same revision.
// This executes the supported JSON subset, not the full upstream conformance suite.
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Respire.IntegrationTests;
using Respire.Json;
using Respire.Samples.VectorData;
using TUnit.Core;

namespace Respire.VectorData.Tests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JsonCollectionTests(ModernRedisTestContainer fixture)
{
    [Test, Arguments(2, false), Arguments(2, true), Arguments(3, false), Arguments(3, true)]
    public Task GetAsync_single_record(int protocol, bool includeVectors) => WithCollection(protocol, async (_, _, collection) =>
    {
        var expected = Movie("one", 1, 0);
        await collection.UpsertAsync(expected);
        AssertRecord(expected, await collection.GetAsync("one", new() { IncludeVectors = includeVectors }), includeVectors);
    });

    [Test, Arguments(2, false), Arguments(2, true), Arguments(3, false), Arguments(3, true)]
    public Task GetAsync_multiple_records_with_missing_keys_returns_only_existing(int protocol, bool includeVectors) => WithCollection(protocol, async (_, _, collection) =>
    {
        JsonMovie[] expected = [Movie("one", 1, 0), Movie("two", 0, 1)];
        await collection.UpsertAsync(expected);
        var received = await Collect(collection.GetAsync(new[] { "one", "missing", "two" }, new() { IncludeVectors = includeVectors }));
        received.Should().HaveCount(2);
        foreach (var record in expected) AssertRecord(record, received.Single(value => value.Id == record.Id), includeVectors);
        (await collection.GetAsync("missing")).Should().BeNull();
    });

    [Test, Arguments(2), Arguments(3)]
    public Task Insert_and_update_in_same_batch(int protocol) => WithCollection(protocol, async (_, _, collection) =>
    {
        await collection.UpsertAsync(Movie("existing", 1, 0));
        JsonMovie[] records = [Movie("new", 1, 1), Movie("existing", 0, 1) with { Details = new("Updated record") }];
        await collection.UpsertAsync(records);
        foreach (var record in records) AssertRecord(record, await collection.GetAsync(record.Id, new() { IncludeVectors = true }), true);
    });

    [Test, Arguments(2), Arguments(3)]
    public Task Delete_single_and_multiple_records(int protocol) => WithCollection(protocol, async (_, _, collection) =>
    {
        await collection.UpsertAsync(new[] { Movie("one", 1, 0), Movie("two", 0, 1) });
        await collection.DeleteAsync("one");
        (await collection.GetAsync("one")).Should().BeNull();
        await collection.DeleteAsync(new[] { "two", "missing" });
        (await Collect(collection.GetAsync(new[] { "one", "two" }))).Should().BeEmpty();
    });

    [Test, Arguments(2), Arguments(3)]
    public Task ReplacementRemovesOptionalDataAndVectorsAndClearsExpiry(int protocol) => WithCollection(protocol, async (client, store, collection) =>
    {
        var original = Movie("one", 1, 0) with { Details = new("Old", "old") };
        await collection.UpsertAsync(original);
        var key = store.DocumentPrefix(collection.Name) + RespireVectorStore.EncodeName("one");
        await client.Keys.ExpireAsync(key, TimeSpan.FromMinutes(1));
        var replacement = new JsonMovie("one", new("New"), [0, 1]);
        await collection.UpsertAsync(replacement);
        AssertRecord(replacement, await collection.GetAsync("one", new() { IncludeVectors = true }), true);
        using var json = JsonDocument.Parse((await client.Json.GetJsonAsync(key))!);
        json.RootElement.GetProperty("details").TryGetProperty("genre", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("alternate_embedding", out _).Should().BeFalse();
        using var ttl = await client.ExecuteAsync(RespireCommands.Key.TTL, [key]);
        ttl.AsInteger().Should().Be(-1);
    });

    [Test, Arguments(2), Arguments(3)]
    public Task JsonNumericArraysDifferFromHashBinaryVectors(int protocol) => WithCollection(protocol, async (client, store, collection) =>
    {
        await collection.UpsertAsync(Movie("one", 1, 0));
        store.RegisterMapper(new MovieMapper());
        using var hashes = store.GetHashCollection<Movie>("hashes");
        try
        {
            await hashes.UpsertAsync(new Movie("one", "Title", new float[] { 1, 0 }));
            var jsonKey = store.DocumentPrefix(collection.Name) + RespireVectorStore.EncodeName("one");
            using var document = JsonDocument.Parse((await client.Json.GetJsonAsync(jsonKey))!);
            document.RootElement.GetProperty("embedding").EnumerateArray().Select(value => value.GetSingle()).Should().Equal(1, 0);
            document.RootElement.GetProperty("details").GetProperty("movie_title").GetString().Should().Be("Title one");
            var hashKey = store.DocumentPrefix(hashes.Name) + RespireVectorStore.EncodeName("one");
            using var hashVector = await client.ExecuteAsync(RespireCommands.Hash.HGET, [hashKey, "embedding"]);
            hashVector.AsBytes().Should().Equal(RespireVectorDataFloat32.Encode(new float[] { 1, 0 }));
        }
        finally { await hashes.EnsureCollectionDeletedAsync(); }
    });

    [Test, Arguments(2), Arguments(3)]
    public Task CollectionLifecycleAndUnindexedDeletionStayIsolated(int protocol) => WithCollection(protocol, async (client, store, collection) =>
    {
        using var other = store.GetJsonCollection<JsonMovie>(collection.Name + ":other");
        using var otherStore = new RespireVectorStore(client, "other:" + Guid.NewGuid().ToString("N"));
        otherStore.RegisterMapper(new JsonMovieMapper());
        using var foreign = otherStore.GetJsonCollection<JsonMovie>(collection.Name);
        try
        {
            await collection.EnsureCollectionExistsAsync();
            await collection.EnsureCollectionExistsAsync();
            (await store.CollectionExistsAsync(collection.Name)).Should().BeTrue();
            (await Collect(store.ListCollectionNamesAsync())).Should().ContainSingle().Which.Should().Be(collection.Name);
            await collection.UpsertAsync(Movie("one", 1, 0));
            await other.UpsertAsync(Movie("one", 0, 1));
            await foreign.UpsertAsync(Movie("one", 1, 1));
            await store.EnsureCollectionDeletedAsync(collection.Name);
            (await collection.GetAsync("one")).Should().BeNull();
            (await other.GetAsync("one")).Should().NotBeNull();
            (await foreign.GetAsync("one")).Should().NotBeNull();
            await collection.UpsertAsync(Movie("before-index", 1, 0));
            await collection.EnsureCollectionDeletedAsync();
            (await collection.GetAsync("before-index")).Should().BeNull();
            (await Collect(store.ListCollectionNamesAsync())).Should().BeEmpty();
        }
        finally
        {
            await other.EnsureCollectionDeletedAsync();
            await foreign.EnsureCollectionDeletedAsync();
        }
    });

    [Test, Arguments(2), Arguments(3)]
    public Task SearchAsync_with_multiple_vector_properties(int protocol) => WithCollection(protocol, async (_, _, collection) =>
    {
        await collection.EnsureCollectionExistsAsync();
        await collection.UpsertAsync(new[] { Movie("a", 1, 0), Movie("b", 0, 1), Movie("c", -1, 0) });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        List<VectorSearchResult<JsonMovie>> all;
        do
        {
            all = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 3, new() { VectorProperty = movie => movie.Vector }, deadline.Token));
            if (all.Count < 3) await Task.Delay(20, deadline.Token);
        } while (all.Count < 3);
        all.Select(hit => hit.Record.Id).Should().Equal("a", "b", "c");
        all.Select(hit => hit.Score).Should().Equal(0, 2, 4);
        all.Should().OnlyContain(hit => hit.Record.Vector == null && hit.Record.AlternateVector == null);
        var page = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 1, new() { VectorProperty = movie => movie.Vector, Skip = 1, IncludeVectors = true }));
        AssertRecord(Movie("b", 0, 1), page.Single().Record, true);
        var alternate = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 3, new() { VectorProperty = movie => movie.AlternateVector, IncludeVectors = true }));
        alternate[0].Record.Id.Should().Be("b");
        alternate.Skip(1).Select(hit => hit.Record.Id).Should().BeEquivalentTo("a", "c");
        alternate.Select(hit => hit.Score).Should().Equal(0, 2, 2);
        var threshold = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 3, new() { VectorProperty = movie => movie.Vector, ScoreThreshold = 2.1 }));
        threshold.Select(hit => hit.Record.Id).Should().Equal("a", "b");
    });

    [Test, Arguments(2), Arguments(3)]
    public Task Search_without_explicitly_specified_vector_property_fails(int protocol) => WithCollection(protocol, async (_, _, collection) =>
    {
        Func<Task> search = () => Collect(collection.SearchAsync(new ReadOnlyMemory<float>(new float[] { 1, 0 }), top: 1));
        var exception = (await search.Should().ThrowAsync<InvalidOperationException>()).Which;
        exception.Message.Should().Be($"The '{nameof(JsonMovie)}' type has multiple vector properties, please specify your chosen property via options.");
    });

    [Test, Arguments(2), Arguments(3)]
    public async Task NestedRenamedVectorPathSupportsReadsAndSearch(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        using var store = new RespireVectorStore(client, "nested:" + Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new NestedVectorMapper());
        using var collection = store.GetJsonCollection<NestedVectorRecord>("nested");
        try
        {
            await collection.EnsureCollectionExistsAsync();
            await collection.UpsertAsync(new NestedVectorRecord("one", new([1, 0], "Keep caption")));
            (await collection.GetAsync("one"))!.Data.Values.Should().BeNull();
            var owned = (await collection.GetAsync("one", new() { IncludeVectors = true }))!;
            owned.Data.Values.Should().Equal(1, 0);
            owned.Data.Caption.Should().Be("Keep caption");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            List<VectorSearchResult<NestedVectorRecord>> hits;
            do
            {
                hits = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 1, cancellationToken: deadline.Token));
                if (hits.Count == 0) await Task.Delay(20, deadline.Token);
            } while (hits.Count == 0);
            hits.Single().Record.Data.Caption.Should().Be("Keep caption");
            hits.Single().Record.Data.Values.Should().BeNull();
            hits.Single().Score.Should().Be(0);
            await collection.UpsertAsync(new NestedVectorRecord("one", new([0, 1], "Updated")));
            owned.Data.Values.Should().Equal(1, 0);
        }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    [Test, Arguments(2), Arguments(3)]
    public Task InvalidJsonVectorsFailBeforeReplacingStoredRecord(int protocol) => WithCollection(protocol, async (_, _, collection) =>
    {
        await collection.UpsertAsync(Movie("one", 1, 0));
        Func<Task> wrongDimensions = () => collection.UpsertAsync(Movie("one", 1, 0) with { Vector = [1] });
        await wrongDimensions.Should().ThrowAsync<ArgumentException>();
        Func<Task> nonFinite = () => collection.UpsertAsync(Movie("one", 1, 0) with { Vector = [float.NaN, 0] });
        await nonFinite.Should().ThrowAsync<ArgumentException>();
        AssertRecord(Movie("one", 1, 0), await collection.GetAsync("one", new() { IncludeVectors = true }), true);
    });

    [Test, Arguments(2), Arguments(3)]
    public Task JsonServerFailuresHaveStoreDiagnostics(int protocol) => WithCollection(protocol, async (client, store, collection) =>
    {
        var key = store.DocumentPrefix(collection.Name) + RespireVectorStore.EncodeName("wrong-type");
        await client.Strings.SetAsync(key, "string");
        Func<Task> read = () => collection.GetAsync("wrong-type");
        var error = (await read.Should().ThrowAsync<VectorStoreException>()).Which;
        error.InnerException.Should().BeOfType<RespireServerException>();
        error.CollectionName.Should().Be(collection.Name);
        error.OperationName.Should().Be("GetAsync");
        error.VectorStoreSystemName.Should().Be("redis");
    });

    private async Task WithCollection(int protocol, Func<RespireClient, RespireVectorStore, RespireVectorStoreCollection<JsonMovie>, Task> test)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        using var store = new RespireVectorStore(client, "json:" + Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new JsonMovieMapper());
        using var collection = store.GetJsonCollection<JsonMovie>("映画:a:b");
        try { await test(client, store, collection); }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    private static JsonMovie Movie(string key, float x, float y) => new(key, new("Title " + key), [x, y], [y, x]);

    private static void AssertRecord(JsonMovie expected, JsonMovie? received, bool includeVectors)
    {
        received.Should().NotBeNull();
        received!.Id.Should().Be(expected.Id);
        received.Details.Should().Be(expected.Details);
        if (includeVectors)
        {
            received.Vector.Should().Equal(expected.Vector);
            if (expected.AlternateVector is null) received.AlternateVector.Should().BeNull();
            else received.AlternateVector.Should().Equal(expected.AlternateVector);
        }
        else
        {
            received.Vector.Should().BeNull();
            received.AlternateVector.Should().BeNull();
        }
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> values)
    {
        var items = new List<T>();
        await foreach (var value in values) items.Add(value);
        return items;
    }
}
