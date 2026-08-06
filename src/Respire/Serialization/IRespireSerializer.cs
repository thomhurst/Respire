using System.Buffers;

namespace Respire.Serialization;

/// <summary>
/// Converts values behind <c>GetAsync&lt;T&gt;</c>/<c>SetAsync&lt;T&gt;</c> and friends.
/// <see cref="string"/>, <see cref="byte"/> arrays, and primitives bypass the serializer.
/// </summary>
public interface IRespireSerializer
{
    void Serialize<T>(IBufferWriter<byte> destination, T value);

    T? Deserialize<T>(ReadOnlySpan<byte> payload);
}

public static class RespireSerializer
{
    /// <summary>System.Text.Json with default options.</summary>
    public static IRespireSerializer Default { get; } = new SystemTextJsonSerializer();
}
