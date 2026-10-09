using System.Text.Json.Serialization;
using Respire.Search;
using Respire.VectorData;

namespace Respire.Samples.VectorData;

public sealed record JsonMovie(
    [property: JsonPropertyName("movie_id")] string Id,
    [property: JsonPropertyName("details")] MovieDetails Details,
    [property: JsonPropertyName("embedding")] float[]? Vector = null,
    [property: JsonPropertyName("alternate_embedding")] float[]? AlternateVector = null);

public sealed record MovieDetails(
    [property: JsonPropertyName("movie_title")] string Title,
    [property: JsonPropertyName("genre")] string? Tag = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonMovie))]
[JsonSerializable(typeof(NestedVectorRecord))]
public partial class MovieJsonContext : JsonSerializerContext;

public sealed record NestedVectorRecord(string Id, NestedVectorData Data);
public sealed record NestedVectorData([property: JsonPropertyName("renamed_vector")] float[]? Values, string Caption);

public sealed class NestedVectorMapper() : RespireVectorDataJsonMapper<NestedVectorRecord>(MovieJsonContext.Default.NestedVectorRecord)
{
    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields =>
        [new(nameof(NestedVectorRecord.Data), "embedding", 2) { JsonPath = "$.Data.renamed_vector", Algorithm = RespireSearchVectorAlgorithm.Flat }];
    public override string GetKey(NestedVectorRecord record) => record.Id;
}

public sealed class JsonMovieMapper() : RespireVectorDataJsonMapper<JsonMovie>(MovieJsonContext.Default.JsonMovie)
{
    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; } =
    [
        new(nameof(JsonMovie.Vector), "vector", 2)
        {
            JsonPath = "$.embedding", Algorithm = RespireSearchVectorAlgorithm.Flat, DistanceMetric = RespireSearchDistanceMetric.L2,
        },
        new(nameof(JsonMovie.AlternateVector), "alternate", 2)
        {
            JsonPath = "$.alternate_embedding", Algorithm = RespireSearchVectorAlgorithm.Flat, DistanceMetric = RespireSearchDistanceMetric.L2,
        },
    ];

    public override IReadOnlyList<RespireSearchField> DataFields { get; } =
        [new("$.details.movie_title", RespireSearchFieldType.Text, Alias: "title"), new("$.details.genre", RespireSearchFieldType.Tag, Alias: "tag")];

    public override string GetKey(JsonMovie record) => record.Id;
}
