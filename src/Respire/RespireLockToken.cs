using System.Diagnostics;
using System.Text;

namespace Respire;

/// <summary>
/// A binary-safe lock owner token shared by token commands, owner queries, and managed locks.
/// Equality and hashing use the exact bytes stored in Redis, including for tokens created from text.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct RespireLockToken : IEquatable<RespireLockToken>
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly ReadOnlyMemory<byte> _bytes;

    /// <summary>Creates a token by encoding text as UTF-8.</summary>
    /// <exception cref="EncoderFallbackException">The text contains an unpaired UTF-16 surrogate.</exception>
    public RespireLockToken(string token)
        => _bytes = StrictUtf8.GetBytes(token ?? throw new ArgumentNullException(nameof(token)));

    /// <summary>Creates a token by copying the supplied bytes, including any slice boundaries.</summary>
    public RespireLockToken(ReadOnlyMemory<byte> token) : this(token, copy: true)
    {
    }

    private RespireLockToken(ReadOnlyMemory<byte> bytes, bool copy)
        => _bytes = copy ? bytes.ToArray() : bytes;

    /// <summary>The exact token bytes, sharing storage with this token.</summary>
    /// <remarks>
    /// Do not mutate the underlying storage, including through MemoryMarshal; doing so changes
    /// the token's identity, hash code, and subsequent ownership checks.
    /// </remarks>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Whether this token is empty. Empty and default tokens cannot acquire or modify locks.</summary>
    public bool IsEmpty => _bytes.IsEmpty;

    /// <summary>Creates a UTF-8 token from text.</summary>
    public static implicit operator RespireLockToken(string token) => new(token);

    /// <summary>Creates a token by copying a byte array.</summary>
    public static explicit operator RespireLockToken(byte[] token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new RespireLockToken(token.AsMemory());
    }

    /// <summary>Creates a token by copying read-only bytes.</summary>
    public static explicit operator RespireLockToken(ReadOnlyMemory<byte> token) => new(token);

    /// <summary>Creates a token by copying bytes.</summary>
    public static explicit operator RespireLockToken(Memory<byte> token) => new(token);

    /// <summary>Tests tokens for exact byte equality.</summary>
    public static bool operator ==(RespireLockToken left, RespireLockToken right) => left.Equals(right);

    /// <summary>Tests tokens for byte inequality.</summary>
    public static bool operator !=(RespireLockToken left, RespireLockToken right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(RespireLockToken other) => _bytes.Span.SequenceEqual(other._bytes.Span);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RespireLockToken other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => AsValue().GetHashCode();

    /// <summary>Returns the exact token bytes as uppercase hexadecimal for lossless display.</summary>
    /// <remarks>
    /// Text tokens also use hexadecimal display. Use <see cref="ToUtf8String"/> to recover text.
    /// This method and the debugger display reveal the full token; do not log tokens used as capabilities.
    /// </remarks>
    public override string ToString() => Convert.ToHexString(_bytes.Span);

    /// <summary>Decodes a text token as UTF-8, rejecting invalid byte sequences.</summary>
    /// <exception cref="DecoderFallbackException">The token contains invalid UTF-8.</exception>
    public string ToUtf8String() => StrictUtf8.GetString(_bytes.Span);

    private string DebuggerDisplay => ToString();

    internal RespireValue AsValue() => new(_bytes);

    // Generated tokens and copied Redis replies already own their storage.
    internal static RespireLockToken FromOwnedBytes(byte[] bytes) => new(bytes, copy: false);
}
