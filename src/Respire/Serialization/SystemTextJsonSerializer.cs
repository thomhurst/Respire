using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Respire.Serialization;

/// <summary>
/// The default serializer. Call <see cref="FromContext(JsonSerializerContext)"/> for
/// source-generated, reflection-free serialization.
/// </summary>
public sealed class SystemTextJsonSerializer : IRespireSerializer
{
    private readonly JsonSerializerOptions? _options;
    private readonly JsonSerializerContext? _context;
    [ThreadStatic] private static Utf8JsonWriter? s_writer;
    private static readonly ArrayBufferWriter<byte> DetachedDestination = new();

    /// <summary>
    /// Creates a reflection-capable serializer with the supplied options, or default options.
    /// Use <see cref="FromContext(JsonSerializerContext)"/> for trimmed or NativeAOT applications.
    /// </summary>
    public SystemTextJsonSerializer(JsonSerializerOptions? options = null)
        => _options = options ?? new JsonSerializerOptions();

    /// <summary>Creates a reflection-free serializer from a source-generated context.</summary>
    public static SystemTextJsonSerializer FromContext(JsonSerializerContext context)
        => new(options: null, context: context ?? throw new ArgumentNullException(nameof(context)));

    private SystemTextJsonSerializer(JsonSerializerOptions? options, JsonSerializerContext context)
    {
        _options = options;
        _context = context;
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public void Serialize<T>(IBufferWriter<byte> destination, T value)
    {
        var writer = RentWriter(destination);
        var completed = false;
        try
        {
            JsonSerializer.Serialize(writer, value, GetTypeInfo<T>());
            completed = true;
        }
        finally
        {
            ReturnWriter(writer, completed);
        }
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        => JsonSerializer.Deserialize(payload, GetTypeInfo<T>());

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
    {
        ArgumentNullException.ThrowIfNull(type);
        var writer = RentWriter(destination);
        var completed = false;
        try
        {
            if (_context is not null)
            {
                JsonSerializer.Serialize(writer, value, type, _context);
            }
            else
            {
                JsonSerializer.Serialize(writer, value, type, _options);
            }
            completed = true;
        }
        finally
        {
            ReturnWriter(writer, completed);
        }
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

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private JsonTypeInfo<T> GetTypeInfo<T>()
    {
        return TypeInfoCache<T>.Values.GetValue(this, Resolve);

        [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
        [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
        static JsonTypeInfo<T> Resolve(SystemTextJsonSerializer serializer) => serializer.ResolveTypeInfo<T>();
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private JsonTypeInfo<T> ResolveTypeInfo<T>()
    {
        if (_context is not null)
            return (JsonTypeInfo<T>)(_context.GetTypeInfo(typeof(T))
                ?? throw new NotSupportedException($"The configured JSON context has no metadata for {typeof(T)}."));

        // Match JsonSerializer's first-use freezing, without freezing caller options in the constructor.
        _options!.MakeReadOnly(populateMissingResolver: true);
        return (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));
    }

    private static class TypeInfoCache<T>
    {
        // A generic cache must not root short-lived serializers and their contexts indefinitely.
        internal static readonly ConditionalWeakTable<SystemTextJsonSerializer, JsonTypeInfo<T>> Values = new();
    }

    private static Utf8JsonWriter RentWriter(IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var writer = s_writer;
        // A custom converter can recursively serialize on the same thread.
        s_writer = null;
        if (writer is null) return new Utf8JsonWriter(destination);
        writer.Reset(destination);
        return writer;
    }

    private static void ReturnWriter(Utf8JsonWriter writer, bool completed)
    {
        var flushed = false;
        try
        {
            // Preserve partial-output flushing on converter failure without masking its exception.
            writer.Flush();
            flushed = true;
        }
        catch when (!completed)
        {
            // The serialization exception is already propagating. Discard this writer below.
        }
        finally
        {
            // Detach even on flush failure, so disposal cannot retry a broken caller destination.
            writer.Reset(DetachedDestination);
            if (flushed && s_writer is null) s_writer = writer;
            else writer.Dispose();
        }
    }
}
