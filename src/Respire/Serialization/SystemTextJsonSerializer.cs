using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Respire.Serialization;

/// <summary>
/// The default serializer. Pass a <see cref="JsonSerializerContext"/> for source-generated,
/// reflection-free serialization.
/// </summary>
public sealed class SystemTextJsonSerializer : IRespireSerializer
{
    private readonly JsonSerializerOptions? _options;
    private readonly JsonSerializerContext? _context;

    /// <summary>
    /// Creates a reflection-capable serializer with the supplied options, or default options.
    /// Use <see cref="SystemTextJsonSerializer(JsonSerializerContext)"/> for trimmed or NativeAOT applications.
    /// </summary>
    public SystemTextJsonSerializer(JsonSerializerOptions? options = null)
        => _options = options ?? new JsonSerializerOptions();

    /// <summary>Creates a serializer backed only by source-generated metadata.</summary>
    public SystemTextJsonSerializer(JsonSerializerContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public void Serialize<T>(IBufferWriter<byte> destination, T value)
        => Serialize(destination, typeof(T), value);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        => (T?)Deserialize(typeof(T), payload);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
    {
        ArgumentNullException.ThrowIfNull(type);
        using var writer = new Utf8JsonWriter(destination);
        if (_context is not null)
        {
            JsonSerializer.Serialize(writer, value, type, _context);
            return;
        }

        JsonSerializer.Serialize(writer, value, type, _options);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public object? Deserialize(Type type, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _context is not null
            ? JsonSerializer.Deserialize(payload, type, _context)
            : JsonSerializer.Deserialize(payload, type, _options);
    }
}
