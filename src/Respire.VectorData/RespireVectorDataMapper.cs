using Respire.Search;

namespace Respire.VectorData;

/// <summary>Explicit record keys and index schema shared by hash and JSON mappings.</summary>
public abstract class RespireVectorDataMapper<TRecord> where TRecord : class
{
    /// <summary>Indexed scalar fields. JSON fields use a property path and an explicit query alias.</summary>
    public virtual IReadOnlyList<RespireSearchField> DataFields => [];

    /// <summary>Explicit CLR property names (including dotted nested paths) mapped to existing TEXT field names or JSON aliases in DataFields.</summary>
    public virtual IReadOnlyList<RespireVectorDataTextField> TextFields => [];

    /// <summary>Indexed FLOAT32 vectors, with CLR property names or dotted JSON member paths for vector selection.</summary>
    public abstract IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; }

    /// <summary>Returns the logical string key.</summary>
    public abstract string GetKey(TRecord record);
}

/// <summary>Maps a CLR text property to a full-text indexed field in DataFields.</summary>
public sealed record RespireVectorDataTextField(string PropertyName, string StorageName);
