using System.Text;
using Respire.Search;
using Respire.VectorData;

namespace Respire.Samples.VectorData;

public sealed record Movie(string Id, string Title, ReadOnlyMemory<float> Vector, string? Tag = null);

public sealed class MovieMapper : RespireVectorDataHashMapper<Movie>
{
    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; } =
        [new(nameof(Movie.Vector), "embedding", 2) { Algorithm = RespireSearchVectorAlgorithm.Flat, DistanceMetric = RespireSearchDistanceMetric.L2 }];

    public override IReadOnlyList<RespireSearchField> DataFields { get; } =
        [new("title", RespireSearchFieldType.Text), new("tag", RespireSearchFieldType.Tag)];

    public override string GetKey(Movie record) => record.Id;

    public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(Movie record)
    {
        var fields = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["title"] = Encoding.UTF8.GetBytes(record.Title),
            ["embedding"] = RespireVectorDataFloat32.Encode(record.Vector.Span),
        };
        if (record.Tag is not null) fields.Add("tag", Encoding.UTF8.GetBytes(record.Tag));
        return fields;
    }

    public override Movie Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields)
        => new(key, Encoding.UTF8.GetString(fields["title"].Span),
            fields.TryGetValue("embedding", out var vector) ? RespireVectorDataFloat32.Decode(vector.Span) : default,
            fields.TryGetValue("tag", out var tag) ? Encoding.UTF8.GetString(tag.Span) : null);
}
