using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ProtoBuf.Meta;

namespace Respire.Serialization.Protobuf;

/// <summary>Serializes non-null protobuf root values using protobuf-net.</summary>
public sealed class RespireProtobufSerializer : IRespireSerializer
{
    private readonly TypeModel _model;
    private readonly ConcurrentDictionary<Type, Action<TypeModel, IBufferWriter<byte>, object>> _writers = new();
    private const string Trimming = "protobuf-net requires preserved serialization contracts and model metadata.";
    private const string DynamicCode = "protobuf-net runtime models and runtime Type dispatch require dynamic code.";

    /// <summary>Uses the supplied model, or protobuf-net's default runtime model. Configure it before use.</summary>
    public RespireProtobufSerializer(TypeModel? model = null) => _model = model ?? RuntimeTypeModel.Default;

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public void Serialize<T>(IBufferWriter<byte> destination, T value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(value);
        _model.Serialize(destination, value);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public T? Deserialize<T>(ReadOnlySpan<byte> payload) => _model.Deserialize<T>(payload);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        // TypeModel's object overload chooses the runtime type. Close a generic writer
        // instead so this overload honors the caller's declared contract, including bases.
        var writer = _writers.GetOrAdd(type, CreateWriter);
        writer(_model, destination, value);
    }

    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    private static Action<TypeModel, IBufferWriter<byte>, object> CreateWriter(Type type)
        => typeof(RespireProtobufSerializer).GetMethod(nameof(Write), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(type).CreateDelegate<Action<TypeModel, IBufferWriter<byte>, object>>();

    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    private static void Write<T>(TypeModel model, IBufferWriter<byte> destination, object value)
        => model.Serialize(destination, (T)value);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public object? Deserialize(Type type, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _model.Deserialize(type, payload);
    }
}
