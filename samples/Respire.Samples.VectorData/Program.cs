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
}
