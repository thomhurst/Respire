namespace Respire;

/// <summary>A precondition evaluated by Valkey 9.2+ immediately before executing a transaction.</summary>
/// <remarks>Keys and raw wire operands are copied at creation. Operands do not use the configured serializer.</remarks>
public sealed class RespireCondition
{
    private RespireCondition(string token, RespireKey key, RespireValue operand = default)
    {
        Token = token;
        Key = key.Snapshot();
        Operand = operand.Snapshot();
    }

    internal string Token { get; }
    internal RespireKey Key { get; }
    internal RespireValue Operand { get; }
    internal bool HasOperand => Token is "IFEQ" or "IFNE";

    /// <summary>Require an existing string key whose bytes equal the supplied value (IFEQ).</summary>
    public static RespireCondition ValueEquals(RespireKey key, RespireValue value)
    {
        RespireValue.ThrowIfNull(value, nameof(value));
        return new("IFEQ", key, value);
    }

    /// <summary>Require a missing key or a string key whose bytes differ from the supplied value (IFNE).</summary>
    public static RespireCondition ValueNotEquals(RespireKey key, RespireValue value)
    {
        RespireValue.ThrowIfNull(value, nameof(value));
        return new("IFNE", key, value);
    }

    /// <summary>Require an existing key of any type (XX).</summary>
    public static RespireCondition KeyExists(RespireKey key) => new("XX", key);

    /// <summary>Require a missing key (NX).</summary>
    public static RespireCondition KeyNotExists(RespireKey key) => new("NX", key);
}
