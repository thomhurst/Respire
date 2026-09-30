namespace Respire;

/// <summary>A value or digest comparison for conditional SET and DELEX commands.</summary>
/// <remarks>Operands use raw wire values, not the configured serializer. Binary operands are copied at creation.
/// Unsupported commands or conditions surface the server error; no weaker condition or script is substituted.</remarks>
public sealed class RespireValueCondition
{
    private RespireValueCondition(string token, RespireValue operand)
    {
        RespireValue.ThrowIfNull(operand, nameof(operand));
        Token = token;
        Operand = operand.Snapshot();
    }

    internal string Token { get; }
    internal RespireValue Operand { get; }

    /// <summary>Compare the current string bytes for equality. SET: Redis 8.4+ or Valkey 8.1+; DELEX: Redis 8.4+.</summary>
    public static RespireValueCondition EqualTo(RespireValue value) => new("IFEQ", value);

    /// <summary>Compare the current string bytes for inequality. SET and DELEX: Redis 8.4+.</summary>
    /// <remarks>SET creates a missing key; DELEX returns false for a missing key.</remarks>
    public static RespireValueCondition NotEqualTo(RespireValue value) => new("IFNE", value);

    /// <summary>Compare the current string digest for equality. SET and DELEX: Redis 8.4+.</summary>
    /// <remarks>Pass the hexadecimal result of DigestAsync. The server validates the digest format.</remarks>
    public static RespireValueCondition DigestEqualTo(string digest) => new("IFDEQ", digest);

    /// <summary>Compare the current string digest for inequality. SET and DELEX: Redis 8.4+.</summary>
    /// <remarks>SET creates a missing key; DELEX returns false for a missing key. The server validates the digest format.</remarks>
    public static RespireValueCondition DigestNotEqualTo(string digest) => new("IFDNE", digest);
}
