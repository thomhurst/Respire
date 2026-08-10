using System.Buffers;

namespace Respire.Serialization;

/// <summary>
/// Converts values behind <c>GetAsync&lt;T&gt;</c>/<c>SetAsync&lt;T&gt;</c> and friends.
/// <see cref="string"/>, <see cref="byte"/> arrays, Boolean values, and numeric types bypass
/// the serializer.
/// </summary>
public interface IRespireSerializer
{
    /// <summary>Serializes a value into the destination buffer.</summary>
    void Serialize<T>(IBufferWriter<byte> destination, T value);

    /// <summary>Deserializes a value from its stored payload.</summary>
    T? Deserialize<T>(ReadOnlySpan<byte> payload);
}

/// <summary>Built-in serializer instances.</summary>
public static class RespireSerializer
{
    /// <summary>System.Text.Json with default options.</summary>
    public static IRespireSerializer Default { get; } = new SystemTextJsonSerializer();
}
