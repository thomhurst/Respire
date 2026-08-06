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

    public SystemTextJsonSerializer(JsonSerializerOptions? options = null)
        => _options = options ?? JsonSerializerOptions.Default;

    public SystemTextJsonSerializer(JsonSerializerContext context)
        => _options = (context ?? throw new ArgumentNullException(nameof(context))).Options;

    public void Serialize<T>(IBufferWriter<byte> destination, T value)
    {
        using var writer = new Utf8JsonWriter(destination);
        JsonSerializer.Serialize(writer, value, _options);
    }

    public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        => JsonSerializer.Deserialize<T>(payload, _options);
}
