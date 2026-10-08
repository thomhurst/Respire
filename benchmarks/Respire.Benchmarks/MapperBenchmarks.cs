using BenchmarkDotNet.Attributes;
using Respire.Json;
using Respire.Search;

namespace Respire.Benchmarks;

[RespireHash("mapper:{Id}")]
public partial record MapperHashModel(string Id, string Name, int Count, bool Enabled, string? Note);

[RespireHash("mapper-vector:{Id}")]
[RespireSearch("mapper-hash", Prefixes = ["mapper-vector:"])]
public partial record MapperVectorHashModel(string Id,
    [property: RespireSearchField(RespireSearchFieldType.Vector, Dimensions = 16)] byte[]? Embedding);

[RespireJson("mapper-json:{Id}")]
[RespireSearch("mapper-json", Prefixes = ["mapper-json:"])]
public partial record MapperJsonModel(string Id,
    [property: RespireSearchField(RespireSearchFieldType.Text)] string Name,
    [property: RespireSearchField(RespireSearchFieldType.Numeric, Sortable = true)] int Count,
    bool Enabled, string? Note,
    [property: RespireSearchField(RespireSearchFieldType.Vector, Dimensions = 16)] float[]? Embedding);

// Every phase uses the same setup and fixtures. Setup validates both implementations,
// including owned vector snapshots, before BenchmarkDotNet starts measuring.
[MemoryDiagnoser]
[AllStatisticsColumn]
public abstract class MapperBenchmarkInputs
{
    [Params(false, true)]
    public bool Populated { get; set; }

    protected MapperHashModel Hash = null!;
    protected MapperVectorHashModel VectorHash = null!;
    protected MapperJsonModel Json = null!;
    protected Dictionary<string, string> HashFields = null!;
    protected Dictionary<string, byte[]> VectorFields = null!;
    protected byte[] JsonBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        var name = Populated ? "姓名😀 with \"quotes\" and \\slashes" : "Ada";
        var note = Populated ? "optional é text" : null;
        var vector = Populated ? Enumerable.Range(0, 16).Select(i => (float)i / 8).ToArray() : null;
        var id = Populated ? "42:姓名" : "42";
        Hash = new(id, name, 123, true, note);
        VectorHash = new(id, Populated ? Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray() : null);
        Json = new(id, name, 123, true, note, vector);
        HashFields = HandwrittenMapper.ToFields(Hash);
        VectorFields = HandwrittenMapper.ToFields(VectorHash);
        JsonBytes = HandwrittenMapper.ToJson(Json);

        Require(Hash == MapperHashModelHashMapper.FromFields(HashFields), "generated hash read");
        Require(Hash == HandwrittenMapper.FromFields(HashFields), "handwritten hash read");
        var generatedHashFields = MapperHashModelHashMapper.ToFields(Hash);
        Require(HashFields.Count == generatedHashFields.Count &&
            HashFields.All(pair => generatedHashFields.TryGetValue(pair.Key, out var value) && value == pair.Value), "hash write");
        Require(JsonBytes.AsSpan().SequenceEqual(MapperJsonModelJsonMapper.ToJson(Json)), "JSON wire representation");
        CheckJson(MapperJsonModelJsonMapper.FromJson(JsonBytes)!);
        CheckJson(HandwrittenMapper.FromJson(JsonBytes)!);
        CheckVector(MapperVectorHashModelHashMapper.FromFields(VectorFields));
        CheckVector(HandwrittenMapper.VectorFromFields(VectorFields));
        var generatedFields = MapperVectorHashModelHashMapper.ToFields(VectorHash);
        Require(VectorFields.Count == generatedFields.Count && VectorFields.All(pair =>
            generatedFields.TryGetValue(pair.Key, out var value) && pair.Value.AsSpan().SequenceEqual(value)), "binary hash write");
        if (VectorHash.Embedding is not null)
        {
            Require(!ReferenceEquals(generatedFields["Embedding"], VectorHash.Embedding), "generated write snapshot");
            Require(!ReferenceEquals(VectorFields["Embedding"], VectorHash.Embedding), "handwritten write snapshot");
        }
        Require(MapperHashModelHashMapper.GetKey(Hash).Equals(HandwrittenMapper.HashKey(Hash)), "hash key");
        Require(MapperJsonModelJsonMapper.GetKey(Json).Equals(HandwrittenMapper.JsonKey(Json)), "JSON key");
        CheckDefinition(MapperVectorHashModelSearchSchema.Definition, HandwrittenMapper.HashDefinition());
        CheckDefinition(MapperJsonModelSearchSchema.Definition, HandwrittenMapper.JsonDefinition());
        CheckFreshDefinition(() => MapperVectorHashModelSearchSchema.Definition);
        CheckFreshDefinition(() => MapperJsonModelSearchSchema.Definition);
        CheckFreshDefinition(HandwrittenMapper.HashDefinition);
        CheckFreshDefinition(HandwrittenMapper.JsonDefinition);
        MapperVectorHashModelSearchSchema.Validate(VectorHash);
        HandwrittenMapper.Validate(VectorHash);
        MapperJsonModelSearchSchema.Validate(Json);
        HandwrittenMapper.Validate(Json);
        var invalidHash = VectorHash with { Embedding = [1] };
        var invalidJson = Json with { Embedding = Enumerable.Repeat(float.NaN, 16).ToArray() };
        Reject(() => MapperVectorHashModelSearchSchema.Validate(invalidHash));
        Reject(() => HandwrittenMapper.Validate(invalidHash));
        Reject(() => MapperJsonModelSearchSchema.Validate(invalidJson));
        Reject(() => HandwrittenMapper.Validate(invalidJson));
        Reject(() => MapperJsonModelSearchSchema.Validate(Json with { Embedding = [1] }));
        Reject(() => HandwrittenMapper.Validate(Json with { Embedding = [1] }));
        Reject(() => HandwrittenMapper.FromJson("{\"Id\":\"42\",\"Name\":\"Ada\",\"Count\":123,\"Enabled\":true,\"Embedding\":[1]}"u8));
    }

    private void CheckJson(MapperJsonModel copy)
    {
        Require(copy with { Embedding = Json.Embedding } == Json, "JSON scalar read");
        Require(copy.Embedding is null ? Json.Embedding is null : copy.Embedding.AsSpan().SequenceEqual(Json.Embedding), "JSON vector read");
    }

    private void CheckVector(MapperVectorHashModel copy)
    {
        Require(copy.Id == VectorHash.Id, "binary hash scalar read");
        Require(copy.Embedding is null ? VectorHash.Embedding is null : copy.Embedding.AsSpan().SequenceEqual(VectorHash.Embedding), "binary hash vector read");
        if (copy.Embedding is not null) Require(!ReferenceEquals(copy.Embedding, VectorFields["Embedding"]), "read snapshot");
    }

    private static void CheckDefinition(RespireSearchIndexDefinition generated, RespireSearchIndexDefinition handwritten)
    {
        Require(generated.Source == handwritten.Source && generated.Prefixes.SequenceEqual(handwritten.Prefixes) &&
            generated.Fields.SequenceEqual(handwritten.Fields), "Search schema metadata");
    }

    private static void CheckFreshDefinition(Func<RespireSearchIndexDefinition> create)
    {
        var first = create();
        var second = create();
        Require(!ReferenceEquals(first, second) && !ReferenceEquals(first.Prefixes, second.Prefixes) &&
            !ReferenceEquals(first.Fields, second.Fields), "fresh Search definition contract");
    }

    private static void Require(bool condition, string operation)
    {
        if (!condition) throw new InvalidOperationException("Mapper benchmark mismatch: " + operation);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Mapper accepted an invalid vector.");
    }
}

public class GeneratedMapperBenchmarks : MapperBenchmarkInputs
{
    [Benchmark] public Dictionary<string, string> HashWrite() => MapperHashModelHashMapper.ToFields(Hash);
    [Benchmark] public MapperHashModel HashRead() => MapperHashModelHashMapper.FromFields(HashFields);
    [Benchmark] public RespireKey HashKey() => MapperHashModelHashMapper.GetKey(Hash);
    [Benchmark] public byte[] JsonWrite() => MapperJsonModelJsonMapper.ToJson(Json);
    [Benchmark] public MapperJsonModel? JsonRead() => MapperJsonModelJsonMapper.FromJson(JsonBytes);
    [Benchmark] public RespireKey JsonKey() => MapperJsonModelJsonMapper.GetKey(Json);
    [Benchmark] public Dictionary<string, byte[]> VectorHashWrite() => MapperVectorHashModelHashMapper.ToFields(VectorHash);
    [Benchmark] public MapperVectorHashModel VectorHashRead() => MapperVectorHashModelHashMapper.FromFields(VectorFields);
    [Benchmark] public RespireSearchIndexDefinition HashDefinition() => MapperVectorHashModelSearchSchema.Definition;
    [Benchmark] public RespireSearchIndexDefinition JsonDefinition() => MapperJsonModelSearchSchema.Definition;
    [Benchmark] public MapperVectorHashModel HashValidate() { MapperVectorHashModelSearchSchema.Validate(VectorHash); return VectorHash; }
    [Benchmark] public MapperJsonModel JsonValidate() { MapperJsonModelSearchSchema.Validate(Json); return Json; }
}

public class HandwrittenMapperBenchmarks : MapperBenchmarkInputs
{
    [Benchmark] public Dictionary<string, string> HashWrite() => HandwrittenMapper.ToFields(Hash);
    [Benchmark] public MapperHashModel HashRead() => HandwrittenMapper.FromFields(HashFields);
    [Benchmark] public RespireKey HashKey() => HandwrittenMapper.HashKey(Hash);
    [Benchmark] public byte[] JsonWrite() => HandwrittenMapper.ToJson(Json);
    [Benchmark] public MapperJsonModel? JsonRead() => HandwrittenMapper.FromJson(JsonBytes);
    [Benchmark] public RespireKey JsonKey() => HandwrittenMapper.JsonKey(Json);
    [Benchmark] public Dictionary<string, byte[]> VectorHashWrite() => HandwrittenMapper.ToFields(VectorHash);
    [Benchmark] public MapperVectorHashModel VectorHashRead() => HandwrittenMapper.VectorFromFields(VectorFields);
    [Benchmark] public RespireSearchIndexDefinition HashDefinition() => HandwrittenMapper.HashDefinition();
    [Benchmark] public RespireSearchIndexDefinition JsonDefinition() => HandwrittenMapper.JsonDefinition();
    [Benchmark] public MapperVectorHashModel HashValidate() { HandwrittenMapper.Validate(VectorHash); return VectorHash; }
    [Benchmark] public MapperJsonModel JsonValidate() { HandwrittenMapper.Validate(Json); return Json; }
}
