using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Respire.Serialization;

/// <summary>
/// The default serializer. Pass a <see cref="JsonSerializerContext"/> for source-generated,
/// reflection-free serialization.
/// </summary>
public sealed class SystemTextJsonSerializer : IRespireSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates a serializer using options or <see cref="JsonSerializerOptions.Default"/>.</summary>
    public SystemTextJsonSerializer(JsonSerializerOptions? options = null)
        => _options = options ?? JsonSerializerOptions.Default;

    /// <summary>Creates a reflection-free serializer from a source-generated context.</summary>
    public SystemTextJsonSerializer(JsonSerializerContext context)
        => _options = (context ?? throw new ArgumentNullException(nameof(context))).Options;

    /// <inheritdoc/>
    public void Serialize<T>(IBufferWriter<byte> destination, T value)
    {
        using var writer = new Utf8JsonWriter(destination);
        JsonSerializer.Serialize(writer, value, _options);
    }

    /// <inheritdoc/>
    public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        => JsonSerializer.Deserialize<T>(payload, _options);
}
