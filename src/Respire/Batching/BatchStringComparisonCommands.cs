using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

public partial interface IBatchStringCommands
{
    /// <summary>Queues a raw SET with a value/digest comparison; returns whether the value was written.</summary>
    /// <remarks>Keep binary value buffers unchanged until execution completes. The condition owns its comparison operand.</remarks>
    RespirePending<bool> SetConditional(RespireKey key, RespireValue value, RespireValueCondition condition, RespireExpiry expiry = default);
    /// <summary>Queues a serialized SET with a raw value/digest comparison; returns whether the value was written.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<bool> SetConditional<T>(RespireKey key, T value, RespireValueCondition condition, RespireExpiry expiry = default);
    /// <summary>Queues a conditional SET GET; returns the owned old string even when the condition fails.</summary>
    RespirePending<string?> GetAndSetConditional(RespireKey key, RespireValue value, RespireValueCondition condition, RespireExpiry expiry = default);
    /// <summary>Queues a conditional serialized SET GET; deserializes the old value even when the raw condition fails.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> GetAndSetConditional<T>(RespireKey key, T value, RespireValueCondition condition, RespireExpiry expiry = default);
    /// <summary>Queues conditional deletion of a string. Redis 8.4+: DELEX.</summary>
    RespirePending<bool> DeleteConditional(RespireKey key, RespireValueCondition condition);
    /// <summary>Queues deletion only when the string equals the raw operand. Valkey 9.0+: DELIFEQ.</summary>
    /// <remarks>Keep binary operand buffers unchanged until execution completes.</remarks>
    RespirePending<bool> DeleteIfEqual(RespireKey key, RespireValue value);
    /// <summary>Queues the hexadecimal digest of a string; returns an owned string, or null when missing. Redis 8.4+: DIGEST.</summary>
    RespirePending<string?> Digest(RespireKey key);
}

internal sealed partial class BatchStringCommands
{
    public RespirePending<bool> SetConditional(RespireKey key, RespireValue value, RespireValueCondition condition, RespireExpiry expiry = default)
        => sink.Add<ConditionalSetCommand, bool>("SET", StringCommands.CompareSetCommand(sink.Client, key, value, condition, expiry, false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<bool> SetConditional<T>(RespireKey key, T value, RespireValueCondition condition, RespireExpiry expiry = default)
        => SetConditional(key, sink.Client.Serialize(value), condition, expiry);

    public RespirePending<string?> GetAndSetConditional(RespireKey key, RespireValue value, RespireValueCondition condition, RespireExpiry expiry = default)
        => sink.Add<ConditionalSetCommand, string?>("SET", StringCommands.CompareSetCommand(sink.Client, key, value, condition, expiry, true),
            static (c, v) => ResponseReader.StringOrNull(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?> GetAndSetConditional<T>(RespireKey key, T value, RespireValueCondition condition, RespireExpiry expiry = default)
        => sink.Add<ConditionalSetCommand, T?>("SET", StringCommands.CompareSetCommand(sink.Client, key, sink.Client.Serialize(value), condition, expiry, true),
            static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<bool> DeleteConditional(RespireKey key, RespireValueCondition condition)
        => sink.Add<Cmd3, bool>("DELEX", StringCommands.CompareDeleteCommand(sink.Client, key, condition),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> DeleteIfEqual(RespireKey key, RespireValue value)
        => sink.Add<Cmd2, bool>("DELIFEQ", StringCommands.DeleteIfEqualCommand(sink.Client, key, value),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<string?> Digest(RespireKey key)
        => sink.Add<Cmd1, string?>("DIGEST", new Cmd1(RespireCommands.String.DIGEST.Verb, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));
}
