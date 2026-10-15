using Microsoft.Extensions.VectorData;
using Respire;
using Respire.Samples.VectorData;
using Respire.VectorData;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
foreach (var protocol in new[] { RespireProtocol.Resp2, RespireProtocol.Resp3 })
{
    var options = RespireOptions.Parse(args.Length == 0 ? "redis://localhost:6379" : args[0]) with { Protocol = protocol };
    await using var client = await RespireClient.ConnectAsync(options, deadline.Token);
    using var store = new RespireVectorStore(client, "sample:vector:" + Guid.NewGuid().ToString("N") + ":");
    store.RegisterMapper(new MovieMapper());
    using var collection = store.GetHashCollection<Movie>("movies");
    try
    {
        await collection.EnsureCollectionExistsAsync(deadline.Token);
        await collection.UpsertAsync(new Movie("1", "Arrival", new float[] { 1, 0 }), deadline.Token);
        var record = await collection.GetAsync("1", new RecordRetrievalOptions { IncludeVectors = true }, deadline.Token);
        if (record?.Title != "Arrival" || record.Vector.Length != 2) throw new InvalidOperationException("Hash mapping smoke failed.");
        var title = "Arrival";
        while (true)
        {
            var found = false;
            await foreach (var result in collection.SearchAsync(new Memory<float>([1, 0]), 1,
                new() { IncludeVectors = true, VectorProperty = movie => movie.Vector, Filter = movie => movie.Title == title && movie.Tag == null }, deadline.Token))
            {
                if (result.Record.Id != "1" || result.Score != 0 || result.Record.Vector.Length != 2)
                    throw new InvalidOperationException("KNN smoke failed.");
                found = true;
            }
            if (found) break;
            await Task.Delay(20, deadline.Token);
        }
        var retrieved = 0;
        await foreach (var result in collection.GetAsync(movie => movie.Title == title, 1, cancellationToken: deadline.Token))
        {
            if (result.Id != "1") throw new InvalidOperationException("Filtered retrieval smoke failed.");
            retrieved++;
        }
        if (retrieved != 1) throw new InvalidOperationException("Filtered retrieval smoke failed.");
        var hybridCount = 0;
        await foreach (var result in collection.HybridSearchAsync(new ReadOnlyMemory<float>([1, 0]), ["Arrival"], 1,
            new() { AdditionalProperty = movie => movie.Title, Filter = movie => movie.Title == title, IncludeVectors = true }, deadline.Token))
        {
            if (result.Record.Id != "1" || result.Score is not > 0 || result.Record.Vector.Length != 2)
                throw new InvalidOperationException("Hash hybrid smoke failed.");
            hybridCount++;
        }
        if (hybridCount != 1) throw new InvalidOperationException("Hash hybrid smoke failed.");
        Console.WriteLine($"VectorData hash, filtered retrieval, filtered KNN and hybrid smoke passed ({protocol}).");
    }
    finally { await collection.EnsureCollectionDeletedAsync(CancellationToken.None); }

    store.RegisterMapper(new JsonMovieMapper());
    using var jsonCollection = store.GetJsonCollection<JsonMovie>("json-movies");
    try
    {
        await jsonCollection.EnsureCollectionExistsAsync(deadline.Token);
        await jsonCollection.UpsertAsync(new JsonMovie("1", new("Arrival", "science-fiction"), [1, 0], [0, 1]), deadline.Token);
        var jsonRecord = await jsonCollection.GetAsync("1", new() { IncludeVectors = true }, deadline.Token);
        if (jsonRecord?.Details.Title != "Arrival" || jsonRecord.Vector?.Length != 2 || jsonRecord.AlternateVector?.Length != 2)
            throw new InvalidOperationException("Generated JSON mapping smoke failed.");
        var withoutVectors = await jsonCollection.GetAsync("1", cancellationToken: deadline.Token);
        if (withoutVectors?.Details.Title != "Arrival" || withoutVectors.Vector is not null || withoutVectors.AlternateVector is not null)
            throw new InvalidOperationException("JSON vector omission smoke failed.");
        while (true)
        {
            var found = false;
            await foreach (var result in jsonCollection.SearchAsync(new float[] { 1, 0 }, 1,
                new() { IncludeVectors = true, VectorProperty = movie => movie.Vector,
                    Filter = movie => movie.Details.Title == "Arrival" && movie.Details.Tag == "science-fiction" }, deadline.Token))
            {
                if (result.Record.Id != "1" || result.Score != 0 || result.Record.Vector?.Length != 2)
                    throw new InvalidOperationException("JSON KNN smoke failed.");
                found = true;
            }
            if (found) break;
            await Task.Delay(20, deadline.Token);
        }
        var jsonFilteredCount = 0;
        await foreach (var result in jsonCollection.GetAsync(movie => movie.Details.Title == "Arrival", 1, cancellationToken: deadline.Token))
        {
            if (result.Id != "1") throw new InvalidOperationException("JSON filtered retrieval smoke failed.");
            jsonFilteredCount++;
        }
        if (jsonFilteredCount != 1) throw new InvalidOperationException("JSON filtered retrieval smoke failed.");
        var jsonHybridCount = 0;
        await foreach (var result in jsonCollection.HybridSearchAsync(new float[] { 0, 1 }, ["Arrival"], 1,
            new() { AdditionalProperty = movie => movie.Details.Title, VectorProperty = movie => movie.AlternateVector,
                Filter = movie => movie.Details.Tag == "science-fiction" }, deadline.Token))
        {
            if (result.Record.Id != "1" || result.Score is not > 0 || result.Record.AlternateVector is not null)
                throw new InvalidOperationException("JSON hybrid smoke failed.");
            jsonHybridCount++;
        }
        if (jsonHybridCount != 1) throw new InvalidOperationException("JSON hybrid smoke failed.");
        await jsonCollection.UpsertAsync(new JsonMovie("1", new("Updated"), [0, 1]), deadline.Token);
        var updated = await jsonCollection.GetAsync("1", new() { IncludeVectors = true }, deadline.Token);
        if (updated?.Details.Title != "Updated" || updated.Details.Tag is not null || updated.AlternateVector is not null)
            throw new InvalidOperationException("JSON replacement smoke failed.");
        await jsonCollection.DeleteAsync("1", deadline.Token);
        if (await jsonCollection.GetAsync("1", cancellationToken: deadline.Token) is not null)
            throw new InvalidOperationException("JSON deletion smoke failed.");
        Console.WriteLine($"VectorData JSON, filtered retrieval, filtered KNN and filtered hybrid smoke passed ({protocol}).");
    }
    finally { await jsonCollection.EnsureCollectionDeletedAsync(CancellationToken.None); }

    // Root the generated metadata and execute nested vector selection/removal in the native binary.
    // Merely declaring NestedVectorMapper does not establish trimming or NativeAOT coverage.
    store.RegisterMapper(new NestedVectorMapper());
    using var nested = store.GetJsonCollection<NestedVectorRecord>("nested-vectors");
    try
    {
        await nested.EnsureCollectionExistsAsync(deadline.Token);
        await nested.UpsertAsync(new[]
        {
            new NestedVectorRecord("1", new([1, 0], "Arrival")),
            new NestedVectorRecord("2", new([0, 1], "Other")),
        }, deadline.Token);
        if (!await store.CollectionExistsAsync("nested-vectors", deadline.Token))
            throw new InvalidOperationException("Nested collection lifecycle smoke failed.");
        var records = 0;
        await foreach (var record in nested.GetAsync(new[] { "1", "2" }, new() { IncludeVectors = true }, deadline.Token))
        {
            if (record.Data.Values?.Length != 2) throw new InvalidOperationException("Nested batch retrieval smoke failed.");
            records++;
        }
        if (records != 2) throw new InvalidOperationException("Nested batch retrieval smoke failed.");
        var omitted = await nested.GetAsync("1", cancellationToken: deadline.Token);
        if (omitted?.Data.Values is not null || omitted?.Data.Caption != "Arrival")
            throw new InvalidOperationException("Nested vector omission smoke failed.");
        while (true)
        {
            var found = false;
            await foreach (var result in nested.SearchAsync(new ReadOnlyMemory<float>([1, 0]), 1,
                new() { IncludeVectors = true, VectorProperty = record => record.Data.Values }, deadline.Token))
            {
                if (result.Record.Id != "1" || result.Score != 0 || result.Record.Data.Values?.Length != 2)
                    throw new InvalidOperationException("Nested HNSW search smoke failed.");
                found = true;
            }
            if (found) break;
            await Task.Delay(20, deadline.Token);
        }
        var hybridCount = 0;
        await foreach (var result in nested.HybridSearchAsync(new Memory<float>([1, 0]), ["Arrival"], 1,
            new() { VectorProperty = record => record.Data.Values, AdditionalProperty = record => record.Data.Caption }, deadline.Token))
        {
            if (result.Record.Id != "1" || result.Score is not > 0 || result.Record.Data.Values is not null)
                throw new InvalidOperationException("Nested hybrid search smoke failed.");
            hybridCount++;
        }
        if (hybridCount != 1) throw new InvalidOperationException("Nested hybrid search smoke failed.");
        await nested.DeleteAsync(new[] { "1", "2" }, deadline.Token);
        if (await nested.GetAsync("1", cancellationToken: deadline.Token) is not null
            || await nested.GetAsync("2", cancellationToken: deadline.Token) is not null)
            throw new InvalidOperationException("Nested batch deletion smoke failed.");
        Console.WriteLine($"VectorData nested JSON, batch CRUD, HNSW and hybrid smoke passed ({protocol}).");
    }
    finally { await store.EnsureCollectionDeletedAsync("nested-vectors", CancellationToken.None); }
}
