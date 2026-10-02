using System.Text.Json.Serialization;
using Respire;
using Respire.Extensions.Json;
using Redis.Search;

var endpoint = args.Length > 0 ? args[0] : "127.0.0.1:6379";
foreach (var protocol in new[] { RespProtocol.Resp2, RespProtocol.Resp3 })
{
    await using var client = await RespireClient.ConnectAsync($"redis://{endpoint}?protocol={(int)protocol}");
    var commands = new ISmokeCommandsImplementation(client);
    var key = "respire:generator-smoke:" + Guid.NewGuid().ToString("N");
    try
    {
        await commands.Set(key, "generated");
        if (await commands.Get(key) != "generated") throw new InvalidOperationException("Generated GET failed.");
        var values = await commands.MultiGet(key, key + ":missing");
        if (values.Length != 2 || values[0] != "generated" || values[1] is not null)
            throw new InvalidOperationException("Generated aggregate reply failed.");
        using var raw = await commands.RawGet(key);
        if (raw.AsString() != "generated") throw new InvalidOperationException("Generated raw reply failed.");

        var index = "respire:search-smoke:" + Guid.NewGuid().ToString("N");
        var documentPrefix = index + ":doc:";
        var vector = new byte[sizeof(float) * 2];
        BitConverter.TryWriteBytes(vector.AsSpan(0, sizeof(float)), 1f);
        BitConverter.TryWriteBytes(vector.AsSpan(sizeof(float), sizeof(float)), 0f);
        var search = new RespireSearchClient(client);
        await search.CreateIndexAsync(index, new RespireSearchIndexDefinition
        {
            Prefixes = [documentPrefix],
            Fields =
            [
                new("title", RespireSearchFieldType.Text),
                new("category", RespireSearchFieldType.Tag),
                new("embedding", RespireSearchFieldType.Vector)
                {
                    Vector = new(RespireSearchVectorAlgorithm.Flat, RespireSearchVectorType.Float32, 2, RespireSearchDistanceMetric.L2),
                },
            ],
        });
        try
        {
            await client.Hashes.SetAsync(documentPrefix + "1",
                ("title", "redis search"), ("category", "cache"), ("embedding", vector));
            await client.Hashes.SetAsync(documentPrefix + "2",
                ("title", "redis client"), ("category", "client"), ("embedding", vector));

            var found = await search.SearchAsync(index, new("redis", new() { Limit = (0, 10) }));
            if (found.Total != 2 || found.Documents.Count != 2)
                throw new InvalidOperationException("Respire.Search FT.SEARCH failed.");

            var groups = await search.AggregateAsync(index, "*", new()
            {
                Stages =
                [
                    RespireSearchAggregateStage.GroupBy(["@category"], new RespireSearchReducer("COUNT", [], "count")),
                    RespireSearchAggregateStage.SortBy(new RespireSearchAggregateSort("@category")),
                ],
            });
            if (groups.Total != 2 || groups.Rows.Count != 2 || groups.Rows[0]["category"] != "cache")
                throw new InvalidOperationException("Respire.Search FT.AGGREGATE failed.");

            var page = await search.AggregateWithCursorAsync(index, "*",
                new() { Stages = [RespireSearchAggregateStage.Load("@title")] },
                new() { Count = 1 });
            var cursorRows = page.Result.Rows.Count;
            while (!page.IsComplete)
            {
                page = await search.ReadCursorAsync(page);
                cursorRows += page.Result.Rows.Count;
            }

            if (cursorRows != 2) throw new InvalidOperationException($"Respire.Search cursor paging returned {cursorRows} rows.");

            var pagedRows = 0;
            await foreach (var rows in search.AggregatePagesAsync(index, "*",
                new() { Stages = [RespireSearchAggregateStage.Load("@title")] },
                new() { Count = 1 }))
            {
                pagedRows += rows.Rows.Count;
            }

            if (pagedRows != 2) throw new InvalidOperationException($"Respire.Search AggregatePagesAsync returned {pagedRows} rows.");

            var info = await search.GetIndexInfoAsync(index);
            if (info.Name != index || info.DocumentCount != 2 || info.Attributes.Count != 3 || info.Attributes[2].Type != "VECTOR")
                throw new InvalidOperationException("Respire.Search FT.INFO parsing failed.");

            var nearest = await search.VectorSearchAsync(index, new("embedding", vector, 1));
            if (nearest.Documents.Count != 1)
                throw new InvalidOperationException("Respire.Search vector query failed.");

            var filtered = await search.VectorSearchAsync(index,
                new("embedding", vector, 2) { Filter = RespireSearchQueryBuilder.Tag("category", "client") });
            if (filtered.Documents.Count != 1 || filtered.Documents[0].Id != documentPrefix + "2")
                throw new InvalidOperationException("Respire.Search filtered vector query failed.");

            var hybrid = await search.HybridSearchAsync(index,
                new("redis", "embedding", vector, 1, 2) { LoadFields = ["title"] });
            if (hybrid.Documents.Count == 0 || !hybrid.Documents[0].Id.StartsWith(documentPrefix, StringComparison.Ordinal) || hybrid.Documents[0].Fields["title"] is null)
                throw new InvalidOperationException($"Respire.Search hybrid query returned no documents (total {hybrid.Total}).");

            try
            {
                await search.SearchAsync(index + ":missing", new("*"));
                throw new InvalidOperationException("Respire.Search server error was not surfaced.");
            }
            catch (RespireServerException)
            {
            }

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                await search.SearchAsync(index, new("*"), canceled.Token);
                throw new InvalidOperationException("Respire.Search cancellation was not propagated.");
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            await search.DropIndexAsync(index, deleteDocuments: true);
        }
    }
    finally
    {
        await commands.Delete(key);
    }

    // Respire.Json under Native AOT: caller-supplied metadata, generated module commands, and key prefixes.
    var jsonPrefix = "respire:json-smoke:" + Guid.NewGuid().ToString("N") + ":";
    var json = new RespireJsonClient(client.WithKeyPrefix(jsonPrefix));
    try
    {
        if (!await json.SetAsync("doc", new SmokeDocument("generated", 1), SmokeJsonContext.Default.SmokeDocument))
            throw new InvalidOperationException("Respire.Json SET failed.");
        var document = await json.GetAsync("doc", SmokeJsonContext.Default.SmokeDocument, RespireJsonPath.JsonPathRoot);
        if (!document.Found || document.Value != new SmokeDocument("generated", 1))
            throw new InvalidOperationException("Respire.Json GET failed.");
        await json.MultiSetAsync([new("doc", new SmokeDocument("multi", 2))], SmokeJsonContext.Default.SmokeDocument);
        var documents = await json.MultiGetAsync(["doc", "missing"], SmokeJsonContext.Default.SmokeDocument);
        if (documents[0]?[0].Value?.Count != 2 || documents[1] is not null)
            throw new InvalidOperationException("Respire.Json MGET failed.");
    }
    finally
    {
        await json.DeleteAsync("doc");
    }
}
Console.WriteLine("Generated commands and Respire.Json passed with RESP2 and RESP3.");

public sealed record SmokeDocument(string Name, int Count);

[JsonSerializable(typeof(SmokeDocument))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;

[RespireCommands]
public interface ISmokeCommands
{
    [RespireCommand("SET")]
    ValueTask Set(RespireKey key, string value, CancellationToken cancellationToken = default);
    [RespireCommand("GET")]
    ValueTask<string?> Get(RespireKey key);
    [RespireCommand("MGET")]
    ValueTask<string?[]> MultiGet(params RespireKey[] keys);
    [RespireCommand("GET")]
    ValueTask<RespireResult> RawGet(RespireKey key);
    [RespireCommand("DEL")]
    ValueTask<long> Delete(RespireKey key);
}
