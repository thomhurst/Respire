using Microsoft.Extensions.VectorData;
using Respire;
using Respire.Samples.VectorData;
using Respire.VectorData;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
foreach (var protocol in new[] { RespProtocol.Resp2, RespProtocol.Resp3 })
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
            await foreach (var result in collection.SearchAsync(new float[] { 1, 0 }, 1,
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
        Console.WriteLine($"VectorData hash, filtered retrieval and filtered KNN smoke passed ({protocol}).");
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
                new() { IncludeVectors = true, VectorProperty = movie => movie.Vector }, deadline.Token))
            {
                if (result.Record.Id != "1" || result.Score != 0 || result.Record.Vector?.Length != 2)
                    throw new InvalidOperationException("JSON KNN smoke failed.");
                found = true;
            }
            if (found) break;
            await Task.Delay(20, deadline.Token);
        }
        await jsonCollection.UpsertAsync(new JsonMovie("1", new("Updated"), [0, 1]), deadline.Token);
        var updated = await jsonCollection.GetAsync("1", new() { IncludeVectors = true }, deadline.Token);
        if (updated?.Details.Title != "Updated" || updated.Details.Tag is not null || updated.AlternateVector is not null)
            throw new InvalidOperationException("JSON replacement smoke failed.");
        await jsonCollection.DeleteAsync("1", deadline.Token);
        if (await jsonCollection.GetAsync("1", cancellationToken: deadline.Token) is not null)
            throw new InvalidOperationException("JSON deletion smoke failed.");
        Console.WriteLine($"VectorData JSON and KNN smoke passed ({protocol}).");
    }
    finally { await jsonCollection.EnsureCollectionDeletedAsync(CancellationToken.None); }
}
