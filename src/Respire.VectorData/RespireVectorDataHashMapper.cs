using System.Buffers.Binary;
using Respire.Search;

namespace Respire.VectorData;

/// <summary>Explicit record mapping without reflection, expression compilation or runtime serialization.</summary>
/// <remarks>Implementations must be thread-safe. Returned field buffers must remain unchanged until the operation completes.</remarks>
public abstract class RespireVectorDataHashMapper<TRecord> where TRecord : class
{
    /// <summary>Indexed scalar fields. Unindexed hash fields may also be written by the mapper.</summary>
    public virtual IReadOnlyList<RespireSearchField> DataFields => [];

    /// <summary>Indexed FLOAT32 vector fields, with explicit CLR property names for vector selection.</summary>
    public abstract IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; }

    /// <summary>Returns the logical string key.</summary>
    public abstract string GetKey(TRecord record);

    /// <summary>Maps all present fields. Omitted fields are removed when a record is replaced.</summary>
    public abstract IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(TRecord record);

    /// <summary>Creates an owned record. Vector fields are absent unless the caller requested vectors.</summary>
    public abstract TRecord Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields);
}

/// <summary>Explicit vector schema with a CLR property name and Redis hash field name.</summary>
public sealed record RespireVectorDataVectorField(string PropertyName, string StorageName, int Dimensions)
{
    /// <summary>Distance function returned unchanged as the result score. Lower is better.</summary>
    public RespireSearchDistanceMetric DistanceMetric { get; init; } = RespireSearchDistanceMetric.Cosine;

    /// <summary>Index algorithm.</summary>
    public RespireSearchVectorAlgorithm Algorithm { get; init; } = RespireSearchVectorAlgorithm.Hnsw;
}

/// <summary>Portable little-endian Redis FLOAT32 vector encoding.</summary>
public static class RespireVectorDataFloat32
{
    /// <summary>Copies finite float elements to the binary hash representation.</summary>
    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[checked(vector.Length * sizeof(float))];
        for (var i = 0; i < vector.Length; i++)
        {
            if (!float.IsFinite(vector[i])) throw new ArgumentException("Vector elements must be finite.", nameof(vector));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }
        return bytes;
    }

    /// <summary>Copies binary hash elements into an owned vector.</summary>
    public static ReadOnlyMemory<float> Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0) throw new ArgumentException("Invalid FLOAT32 byte length.", nameof(bytes));
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        return vector;
    }
}
