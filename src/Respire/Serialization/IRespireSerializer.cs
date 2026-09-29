using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Respire.Serialization;

/// <summary>
/// Converts values behind <c>GetAsync&lt;T&gt;</c>/<c>SetAsync&lt;T&gt;</c> and friends.
/// <see cref="string"/>, <see cref="byte"/> arrays, <see cref="char"/> and nullable character
/// values, Boolean values, and numeric types bypass the serializer.
/// </summary>
public interface IRespireSerializer
{
    /// <summary>Writes <paramref name="value"/> to <paramref name="destination"/>.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    void Serialize<T>(IBufferWriter<byte> destination, T value);

    /// <summary>Reads a <typeparamref name="T"/> from <paramref name="payload"/>.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    T? Deserialize<T>(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Writes <paramref name="value"/> using its declared <paramref name="type"/>.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    void Serialize(IBufferWriter<byte> destination, Type type, object? value);

    /// <summary>
    /// Reads a value with the declared <paramref name="type"/> from <paramref name="payload"/>.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    object? Deserialize(Type type, ReadOnlySpan<byte> payload);
}

/// <summary>Built-in serializer instances.</summary>
public static class RespireSerializer
{
    /// <summary>System.Text.Json with default options.</summary>
    public static IRespireSerializer Default { get; } = new SystemTextJsonSerializer();
}
