using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Respire;
using Respire.Json;
using Respire.Search;

VerifyCodecs();
if (args is ["--codecs-only"])
{
    Console.WriteLine("Generated mapper codec conformance passed; Redis operations were not run.");
    return;
}

var endpoint = args.Length > 0 ? args[0] : "127.0.0.1:6379";
foreach (var protocol in new[] { RespProtocol.Resp2, RespProtocol.Resp3 })
{
    await using var client = await RespireClient.ConnectAsync(
        RespireOptions.Parse(endpoint) with { Protocol = protocol });
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var run = Guid.NewGuid().ToString("N");
    await VerifyHashAsync(client, run, deadline.Token);
    await VerifyJsonAsync(client, run, deadline.Token);
    await VerifySearchAsync(client, run, json: false, deadline.Token);
    await VerifySearchAsync(client, run, json: true, deadline.Token);
    Console.WriteLine($"Generated hash/JSON/Search mapper conformance passed ({protocol}).");
}

static void VerifyCodecs()
{
    Check(!JsonSerializer.IsReflectionEnabledByDefault, "Reflection-based JSON serialization must be disabled.");
    var options = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() };
    try
    {
        _ = options.GetTypeInfo(typeof(Unmapped));
        throw new InvalidOperationException("Unmapped types must not acquire dynamic serialization metadata.");
    }
    catch (NotSupportedException) { }

    foreach (var value in new[]
    {
        Scalars.Create("unicode:姓名😀"),
        Scalars.Create("nulls") with { Name = null, Flag = null, Count = null, Age = null,
            Score = null, Amount = null, Token = null, Created = null },
    })
    {
        Check(ScalarsHashMapper.FromFields(ScalarsHashMapper.ToFields(value)) == value, "Hash scalar codec mismatch.");
        Check(ScalarsJsonMapper.FromJson(ScalarsJsonMapper.ToJson(value)) == value, "JSON scalar codec mismatch.");
        Check(JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, ScalarsJsonMapper.JsonTypeInfo),
            ScalarsJsonMapper.JsonTypeInfo) == value, "Explicit JSON metadata mismatch.");
    }
    Check(ScalarsJsonMapper.FromJson("null"u8) is null, "JSON document null mismatch.");
    Check(ScalarsJsonMapper.FromJson(ScalarsJsonMapper.ToJson(null)) is null, "JSON null codec mismatch.");
    var required = new RequiredScalars("required", "", true, int.MinValue, long.MaxValue,
        double.Epsilon, decimal.MinValue, Guid.Empty, DateTimeOffset.MinValue);
    Check(RequiredScalarsHashMapper.FromFields(RequiredScalarsHashMapper.ToFields(required)) == required,
        "Required hash scalar codec mismatch.");
    Check(RequiredScalarsJsonMapper.FromJson(RequiredScalarsJsonMapper.ToJson(required)) == required,
        "Required JSON scalar codec mismatch.");
}

static async Task VerifyHashAsync(IRespireClient client, string run, CancellationToken token)
{
    var value = Scalars.Create(run);
    var key = ScalarsHashMapper.GetKey(value);
    try
    {
        foreach (var mode in new[] { RespireHashExpiryMode.HSetEx, RespireHashExpiryMode.HSetThenExpire })
        {
            await ScalarsHashMapper.SetWithExpiryAsync(client, mode, value, token);
            Check(await ScalarsHashMapper.GetAsync(client, key, token) == value, "Hash model mismatch.");
            Check((await client.Hashes.ExpiryAsync(key, "Name"))[0].HasExpiry, "Hash field TTL missing.");
            await client.Hashes.SetAsync(key, "Unknown", (RespireValue)"preserved");
            var tracker = ScalarsHashMapper.Track(client, value, expiryMode: mode);
            var updated = value with { Name = null, Age = 0 };
            await tracker.UpdateAsync(updated, token);
            await tracker.UpdateAsync(updated, token);
            Check(await ScalarsHashMapper.GetAsync(client, key, token) == updated, "Tracked update mismatch.");
            Check(await client.Hashes.GetStringAsync(key, "Unknown") == "preserved", "Tracked update lost unknown field.");
            var partial = await ScalarsHashMapper.GetPartialAsync(client, key, ["Name", "Age"], token);
            Check(partial.Name.Selected && !partial.Name.Found, "Hash absent nullable field mismatch.");
            Check(partial.Age.Selected && partial.Age.Found && partial.Age.Value == 0, "Hash selected zero mismatch.");
            Check(!partial.Id.Selected, "Hash unselected field mismatch.");
            Check(!(await ScalarsHashMapper.GetPartialAsync(client, key + ":missing", ["Age"], token)).Age.Found,
                "Missing hash partial read mismatch.");
        }
    }
    finally { await client.Keys.DeleteAsync(key); }
}

static async Task VerifyJsonAsync(IRespireClient client, string run, CancellationToken token)
{
    var json = new RespireJsonClient(client);
    var value = Scalars.Create(run);
    var key = ScalarsJsonMapper.GetKey(value);
    try
    {
        Check(await ScalarsJsonMapper.SetAsync(json, value, cancellationToken: token), "JSON write failed.");
        Check((await ScalarsJsonMapper.GetAsync(json, key, token)).Value == value, "JSON model mismatch.");
        await ScalarsJsonMapper.SetAgeAsync(json, key, null, token);
        var partial = await ScalarsJsonMapper.GetAgeAsync(json, key, token);
        Check(partial.Found && partial.Value is null, "JSON explicit null partial read mismatch.");
        await json.DeleteAsync(key, ScalarsJsonMapper.AgePath, token);
        Check(!(await ScalarsJsonMapper.GetAgeAsync(json, key, token)).Found, "JSON missing partial read mismatch.");
        Check(!(await ScalarsJsonMapper.GetAsync(json, key + ":missing", token)).Found, "Missing JSON document mismatch.");
        await ScalarsJsonMapper.SetAsync(json, key, null, cancellationToken: token);
        var document = await ScalarsJsonMapper.GetAsync(json, key, token);
        Check(document.Found && document.Value is null, "JSON explicit document null mismatch.");
    }
    finally { await client.Keys.DeleteAsync(key); }
}

static async Task VerifySearchAsync(IRespireClient client, string run, bool json, CancellationToken token)
{
    var index = $"mapper-smoke:{run}:{(json ? "json" : "hash")}";
    var key = index + ":doc:1";
    var definition = (json ? SearchJsonSearchSchema.Definition : SearchHashSearchSchema.Definition)
        with { Prefixes = [index + ":doc:"] };
    var vector = new byte[8];
    BitConverter.TryWriteBytes(vector.AsSpan(), 1f);
    BitConverter.TryWriteBytes(vector.AsSpan(4), 2f);
    await client.Search.CreateIndexAsync(index, definition, token);
    try
    {
        // Avoid racing the initial scan with the first document write.
        while ((await client.Search.GetIndexInfoAsync(index, token)).Properties["indexing"].Scalar != "0")
            await Task.Delay(20, token);
        if (json)
        {
            var jsonClient = new RespireJsonClient(client);
            await SearchJsonJsonMapper.SetAsync(jsonClient, key, new("1", "redis", 42, [1, 2]), cancellationToken: token);
            var document = await SearchJsonJsonMapper.GetAsync(jsonClient, key, token);
            Check(document.Found && document.Value!.Embedding.SequenceEqual(new float[] { 1, 2 }),
                "JSON vector model read mismatch.");
            var partial = await SearchJsonJsonMapper.GetEmbeddingAsync(jsonClient, key, token);
            Check(partial.Found && partial.Value!.SequenceEqual(new float[] { 1, 2 }), "JSON vector partial read mismatch.");
        }
        else
        {
            await SearchHashHashMapper.SetAsync(client, key, new("1", "redis", 42, vector), token);
            Check((await SearchHashHashMapper.GetAsync(client, key, token))!.Embedding.SequenceEqual(vector),
                "Hash vector model read mismatch.");
            var partial = await SearchHashHashMapper.GetPartialAsync(client, key, ["Embedding", "Rating"], token);
            Check(partial.Embedding.Value!.SequenceEqual(vector) && partial.Rating.Value == 42,
                "Hash vector partial read mismatch.");
        }
        while ((await client.Search.VectorSearchAsync(index, new("embedding", vector, 1), cancellationToken: token)).Documents.Count != 1)
            await Task.Delay(20, token);
        var filter = RespireSearchQueryBuilder.And(RespireSearchQueryBuilder.TextField("title", "redis"),
            RespireSearchQueryBuilder.NumericRange("rating", 42L, 42L));
        var found = await client.Search.SearchAsync(index, new(filter), token);
        Check(found.Documents.Count == 1 && found.Documents[0].Id == key, "Generated Search schema query mismatch.");
        Check((await client.Search.SearchAsync(index,
            new(RespireSearchQueryBuilder.NumericRange("rating", 0L, 1L)), token)).Total == 0, "Search rejection control matched.");
    }
    finally
    {
        await client.Search.DropIndexAsync(index, deleteDocuments: true);
        await client.Keys.DeleteAsync(key);
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class Unmapped;

[RespireHash("mapper-smoke:required-hash:{Id}")]
[RespireJson("mapper-smoke:required-json:{Id}")]
internal partial record RequiredScalars(string Id, string Name, bool Flag, int Age, long Count,
    double Score, decimal Amount, Guid Token, DateTimeOffset Created);

[RespireHash("mapper-smoke:hash:{Id}")]
[RespireJson("mapper-smoke:json:{Id}")]
internal partial record Scalars(string Id, [property: RespireFieldTtl(60000)] string? Name,
    bool? Flag, long? Count, int? Age, double? Score, decimal? Amount, Guid? Token, DateTimeOffset? Created)
{
    internal static Scalars Create(string id) => new(id, "姓名😀", false, long.MinValue, 0, 1.125, 12.50m,
        Guid.Parse("754a1f42-3f49-42e9-9cb6-7e477b3e4098"),
        new DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.FromHours(5.5)));
}

[RespireHash("mapper-smoke:search-hash:{Id}"), RespireSearch("mapper-smoke-hash", Prefixes = ["mapper-smoke:search-hash:"])]
internal partial record SearchHash(string Id,
    [property: RespireSearchField(RespireSearchFieldType.Text, Alias = "title")] string Title,
    [property: RespireSearchField(RespireSearchFieldType.Numeric, Alias = "rating")] int Rating,
    [property: RespireSearchField(RespireSearchFieldType.Vector, Alias = "embedding", Dimensions = 2)] byte[] Embedding);

[RespireJson("mapper-smoke:search-json:{Id}"), RespireSearch("mapper-smoke-json", Prefixes = ["mapper-smoke:search-json:"])]
internal partial record SearchJson(string Id,
    [property: RespireSearchField(RespireSearchFieldType.Text, Alias = "title")] string Title,
    [property: RespireSearchField(RespireSearchFieldType.Numeric, Alias = "rating")] int Rating,
    [property: RespireSearchField(RespireSearchFieldType.Vector, Alias = "embedding", Dimensions = 2)] float[] Embedding);
