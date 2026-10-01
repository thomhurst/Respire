using System.Globalization;
using Respire;

namespace Redis.Search;

/// <summary>Search index source type.</summary>
public enum RespireSearchSource
{
    /// <summary>Index Redis hashes.</summary>
    Hash,
    /// <summary>Index RedisJSON documents.</summary>
    Json,
}

/// <summary>Search field type.</summary>
public enum RespireSearchFieldType
{
    /// <summary>Full text field.</summary>
    Text,
    /// <summary>Exact tag field.</summary>
    Tag,
    /// <summary>Numeric field.</summary>
    Numeric,
    /// <summary>Geospatial field.</summary>
    Geo,
    /// <summary>Geoshape field.</summary>
    GeoShape,
    /// <summary>Vector field.</summary>
    Vector,
}

/// <summary>Vector index algorithm.</summary>
public enum RespireSearchVectorAlgorithm
{
    /// <summary>Brute-force <c>FLAT</c> index.</summary>
    Flat,
    /// <summary>Approximate <c>HNSW</c> index.</summary>
    Hnsw,
}

/// <summary>Vector element type.</summary>
public enum RespireSearchVectorType
{
    /// <summary>32-bit IEEE floating point (<c>FLOAT32</c>).</summary>
    Float32,
    /// <summary>64-bit IEEE floating point (<c>FLOAT64</c>).</summary>
    Float64,
    /// <summary>Brain floating point (<c>BFLOAT16</c>).</summary>
    BFloat16,
    /// <summary>16-bit IEEE floating point (<c>FLOAT16</c>).</summary>
    Float16,
    /// <summary>Signed 8-bit integer (<c>INT8</c>).</summary>
    Int8,
    /// <summary>Unsigned 8-bit integer (<c>UINT8</c>).</summary>
    UInt8,
}

/// <summary>Vector distance metric.</summary>
public enum RespireSearchDistanceMetric
{
    /// <summary>Euclidean distance (<c>L2</c>).</summary>
    L2,
    /// <summary>Inner product (<c>IP</c>).</summary>
    InnerProduct,
    /// <summary>Cosine distance (<c>COSINE</c>).</summary>
    Cosine,
}

/// <summary>Typed vector field schema. The client computes the algorithm argument count.</summary>
/// <param name="Algorithm">Index algorithm.</param>
/// <param name="Type">Vector element type.</param>
/// <param name="Dimensions">Number of vector dimensions.</param>
/// <param name="DistanceMetric">Distance metric.</param>
public sealed record RespireSearchVectorOptions(
    RespireSearchVectorAlgorithm Algorithm,
    RespireSearchVectorType Type,
    int Dimensions,
    RespireSearchDistanceMetric DistanceMetric)
{
    /// <summary>
    /// Additional algorithm attributes, such as <c>M</c>, <c>EF_CONSTRUCTION</c>, or <c>INITIAL_CAP</c>.
    /// Names and values are sent as given and counted automatically.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = RespireSearchEmpty.Strings;

    internal void AddArguments(List<RespireValue> args)
    {
        if (Dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(Dimensions));
        ArgumentNullException.ThrowIfNull(Attributes);
        args.Add(Algorithm switch
        {
            RespireSearchVectorAlgorithm.Flat => "FLAT",
            RespireSearchVectorAlgorithm.Hnsw => "HNSW",
            _ => throw new ArgumentOutOfRangeException(nameof(Algorithm)),
        });
        args.Add(checked(6 + (Attributes.Count * 2)));
        args.Add("TYPE");
        args.Add(Type switch
        {
            RespireSearchVectorType.Float32 => "FLOAT32",
            RespireSearchVectorType.Float64 => "FLOAT64",
            RespireSearchVectorType.BFloat16 => "BFLOAT16",
            RespireSearchVectorType.Float16 => "FLOAT16",
            RespireSearchVectorType.Int8 => "INT8",
            RespireSearchVectorType.UInt8 => "UINT8",
            _ => throw new ArgumentOutOfRangeException(nameof(Type)),
        });
        args.Add("DIM");
        args.Add(Dimensions);
        args.Add("DISTANCE_METRIC");
        args.Add(DistanceMetric switch
        {
            RespireSearchDistanceMetric.L2 => "L2",
            RespireSearchDistanceMetric.InnerProduct => "IP",
            RespireSearchDistanceMetric.Cosine => "COSINE",
            _ => throw new ArgumentOutOfRangeException(nameof(DistanceMetric)),
        });
        foreach (var attribute in Attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Value);
            args.Add(attribute.Key);
            args.Add(attribute.Value);
        }
    }
}

/// <summary>Index schema field definition.</summary>
/// <param name="Identifier">Hash field name or JSON path.</param>
/// <param name="Type">Field type.</param>
/// <param name="Alias">Optional attribute name used in queries.</param>
/// <param name="Sortable">Adds <c>SORTABLE</c>. Not valid for vector fields.</param>
/// <param name="NoIndex">Adds <c>NOINDEX</c>. Not valid for vector fields.</param>
/// <param name="Options">
/// Raw schema tokens sent after the typed options, for server-version-specific settings that have
/// no typed property. Vector fields use either <see cref="Vector"/> or these tokens, not both.
/// </param>
public sealed record RespireSearchField(string Identifier, RespireSearchFieldType Type, string? Alias = null, bool Sortable = false, bool NoIndex = false, IReadOnlyList<string>? Options = null)
{
    /// <summary>Typed vector schema. Only valid for <see cref="RespireSearchFieldType.Vector"/> fields.</summary>
    public RespireSearchVectorOptions? Vector { get; init; }

    /// <summary>Text field weight (<c>WEIGHT</c>). Only valid for text fields.</summary>
    public double? Weight { get; init; }

    /// <summary>Disables stemming (<c>NOSTEM</c>). Only valid for text fields.</summary>
    public bool NoStem { get; init; }

    /// <summary>Tag separator character (<c>SEPARATOR</c>). Only valid for tag fields.</summary>
    public char? Separator { get; init; }

    /// <summary>Keeps tag case (<c>CASESENSITIVE</c>). Only valid for tag fields.</summary>
    public bool CaseSensitive { get; init; }

    internal void AddArguments(List<RespireValue> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Identifier);
        Validate();
        args.Add(Identifier);
        if (!string.IsNullOrWhiteSpace(Alias))
        {
            args.Add("AS");
            args.Add(Alias);
        }

        args.Add(Type switch
        {
            RespireSearchFieldType.Text => "TEXT",
            RespireSearchFieldType.Tag => "TAG",
            RespireSearchFieldType.Numeric => "NUMERIC",
            RespireSearchFieldType.Geo => "GEO",
            RespireSearchFieldType.GeoShape => "GEOSHAPE",
            RespireSearchFieldType.Vector => "VECTOR",
            _ => throw new ArgumentOutOfRangeException(nameof(Type)),
        });

        // Vector algorithm arguments must directly follow the VECTOR type token.
        Vector?.AddArguments(args);
        if (Weight is { } weight)
        {
            args.Add("WEIGHT");
            args.Add(weight.ToString("R", CultureInfo.InvariantCulture));
        }

        if (NoStem) args.Add("NOSTEM");
        if (Separator is { } separator)
        {
            args.Add("SEPARATOR");
            args.Add(separator.ToString());
        }

        if (CaseSensitive) args.Add("CASESENSITIVE");
        if (Options is not null)
        {
            foreach (var option in Options)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(option);
                args.Add(option);
            }
        }

        if (Sortable) args.Add("SORTABLE");
        if (NoIndex) args.Add("NOINDEX");
    }

    private void Validate()
    {
        var isVector = Type == RespireSearchFieldType.Vector;
        var hasRawOptions = Options is { Count: > 0 };
        if (isVector)
        {
            if (Vector is null && !hasRawOptions)
                throw new ArgumentException("Vector fields require Vector options or raw algorithm Options.", nameof(Vector));
            if (Vector is not null && hasRawOptions)
                throw new ArgumentException("Use either typed Vector options or raw Options for a vector field, not both.", nameof(Options));
            if (Sortable || NoIndex)
                throw new ArgumentException("Vector fields cannot be SORTABLE or NOINDEX.", nameof(Sortable));
        }
        else if (Vector is not null)
        {
            throw new ArgumentException("Vector options are only valid for vector fields.", nameof(Vector));
        }

        if (Type != RespireSearchFieldType.Text && (Weight is not null || NoStem))
            throw new ArgumentException("WEIGHT and NOSTEM are only valid for text fields.", nameof(Weight));
        if (Weight is { } weight && (double.IsNaN(weight) || double.IsInfinity(weight) || weight < 0))
            throw new ArgumentOutOfRangeException(nameof(Weight), weight, "Weight must be a finite, non-negative number.");
        if (Type != RespireSearchFieldType.Tag && (Separator is not null || CaseSensitive))
            throw new ArgumentException("SEPARATOR and CASESENSITIVE are only valid for tag fields.", nameof(Separator));
    }
}

/// <summary>Index source, key prefixes, and schema.</summary>
public sealed record RespireSearchIndexDefinition
{
    /// <summary>Source type. Defaults to HASH.</summary>
    public RespireSearchSource Source { get; init; } = RespireSearchSource.Hash;

    /// <summary>Prefixes included in index.</summary>
    public IReadOnlyList<string> Prefixes { get; init; } = [];

    /// <summary>Fields indexed by this definition.</summary>
    public IReadOnlyList<RespireSearchField> Fields { get; init; } = [];

    /// <summary>Builds a complete FT.CREATE argument list.</summary>
    internal RespireValue[] ToArguments()
    {
        ArgumentNullException.ThrowIfNull(Prefixes);
        ArgumentNullException.ThrowIfNull(Fields);
        if (Fields.Count == 0) throw new ArgumentException("At least one schema field is required.", nameof(Fields));
        var source = Source switch
        {
            RespireSearchSource.Hash => "HASH",
            RespireSearchSource.Json => "JSON",
            _ => throw new ArgumentOutOfRangeException(nameof(Source)),
        };
        var args = new List<RespireValue> { "ON", source };
        if (Prefixes.Count > 0)
        {
            args.Add("PREFIX");
            args.Add(Prefixes.Count);
            foreach (var prefix in Prefixes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
                args.Add(prefix);
            }
        }

        args.Add("SCHEMA");
        foreach (var field in Fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            field.AddArguments(args);
        }

        return [.. args];
    }
}
