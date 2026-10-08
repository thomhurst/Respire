namespace Respire.Search;

/// <summary>Generates a reflection-free <c>ModelNameSearchSchema</c> for a hash or JSON model.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RespireSearchAttribute(string indexName) : Attribute
{
    /// <summary>The default index name. Version this name when rebuilding a schema.</summary>
    public string IndexName { get; } = indexName;

    /// <summary>Key prefixes included in the index. At least one explicit prefix is required.</summary>
    public string[] Prefixes { get; set; } = [];
}

/// <summary>Indexes a mapped property with compile-time type and option validation.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RespireSearchFieldAttribute(RespireSearchFieldType type) : Attribute
{
    /// <summary>The Search field type.</summary>
    public RespireSearchFieldType Type { get; } = type;
    /// <summary>Query attribute name. Defaults to the CLR property name.</summary>
    public string? Alias { get; set; }
    /// <summary>Enables sorting. Not supported for vectors.</summary>
    public bool Sortable { get; set; }
    /// <summary>Disables indexing. Not supported for vectors.</summary>
    public bool NoIndex { get; set; }
    /// <summary>Text field weight. Must be finite and non-negative.</summary>
    public double Weight { get; set; } = 1;
    /// <summary>Disables stemming for text.</summary>
    public bool NoStem { get; set; }
    /// <summary>Tag separator. NUL uses the server default.</summary>
    public char Separator { get; set; }
    /// <summary>Preserves case in tags.</summary>
    public bool CaseSensitive { get; set; }
    /// <summary>Vector dimensions. Required for vectors.</summary>
    public int Dimensions { get; set; }
    /// <summary>Vector element type. Hash vectors are binary blobs; JSON supports float[] and double[].</summary>
    public RespireSearchVectorType VectorType { get; set; } = RespireSearchVectorType.Float32;
    /// <summary>Vector index algorithm.</summary>
    public RespireSearchVectorAlgorithm Algorithm { get; set; } = RespireSearchVectorAlgorithm.Flat;
    /// <summary>Vector distance metric.</summary>
    public RespireSearchDistanceMetric DistanceMetric { get; set; } = RespireSearchDistanceMetric.Cosine;
}

/// <summary>Connector-independent metadata and validation for a mapped Search model.</summary>
/// <remarks>VectorStore integrations can consume this interface without a connector dependency or reflection.</remarks>
public interface IRespireSearchSchema<TModel>
{
    /// <summary>The generated default index name.</summary>
    static abstract string IndexName { get; }
    /// <summary>The generated schema, source and prefixes.</summary>
    static abstract RespireSearchIndexDefinition Definition { get; }
    /// <summary>Expands the mapped model's key template.</summary>
    static abstract RespireKey GetKey(TModel value);
    /// <summary>Validates vector dimensions and representations before writing a model.</summary>
    static abstract void Validate(TModel value);
}
