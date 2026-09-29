using System.Text;
using Respire.Internal;

namespace Respire;

/// <summary>
/// An owned, binary-safe pub/sub channel or pattern. Equality compares bytes, independently of
/// subscription kind. The default value is a valid empty literal channel.
/// </summary>
public readonly struct RespireChannel : IEquatable<RespireChannel>
{
    private readonly byte[]? _bytes;

    /// <summary>Creates a literal channel from valid UTF-16 text, encoded as UTF-8.</summary>
    /// <exception cref="ArgumentException">The text contains an unpaired surrogate.</exception>
    public RespireChannel(string value)
    {
        Utf8RouteName.Validate(value);
        _bytes = Encoding.UTF8.GetBytes(value);
    }

    /// <summary>Creates a literal channel by copying the supplied bytes.</summary>
    public RespireChannel(ReadOnlyMemory<byte> value)
        => _bytes = value.IsEmpty ? [] : value.ToArray();

    private RespireChannel(byte[]? bytes, SubscriptionKind kind)
    {
        _bytes = bytes;
        Kind = kind;
    }

    /// <summary>The exact owned channel bytes; accessing them does not allocate.</summary>
    /// <remarks>Do not mutate the underlying storage through MemoryMarshal or other unsafe access.</remarks>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>The explicit subscription command family. Reserved names do not change this value.</summary>
    public SubscriptionKind Kind { get; }

    /// <summary>The Redis Cluster slot computed from the raw bytes, including hash tags.</summary>
    public int ClusterSlot => ClusterHash.GetSlot(_bytes.AsSpan());

    /// <summary>Marks a channel for SUBSCRIBE, without changing its bytes.</summary>
    public static RespireChannel Literal(RespireChannel value) => value.WithKind(SubscriptionKind.Channel);

    /// <summary>Marks a channel pattern for PSUBSCRIBE, without changing its bytes.</summary>
    public static RespireChannel Pattern(RespireChannel value) => value.WithKind(SubscriptionKind.Pattern);

    /// <summary>Marks a channel for SSUBSCRIBE and SPUBLISH, without changing its bytes.</summary>
    public static RespireChannel Sharded(RespireChannel value) => value.WithKind(SubscriptionKind.Sharded);

    /// <summary>Encodes valid UTF-16 text as an owned UTF-8 channel.</summary>
    public static implicit operator RespireChannel(string value) => new(value);

    /// <summary>Copies a byte array into an owned channel.</summary>
    public static implicit operator RespireChannel(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value.AsMemory());
    }

    /// <summary>Copies bytes into an owned channel.</summary>
    public static implicit operator RespireChannel(ReadOnlyMemory<byte> value) => new(value);

    /// <summary>Compares the exact bytes, independently of subscription kind.</summary>
    public bool Equals(RespireChannel other) => _bytes.AsSpan().SequenceEqual(other._bytes.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RespireChannel other && Equals(other);

    /// <summary>Returns a process-local hash of the channel bytes.</summary>
    /// <remarks>The hash is seeded per process. Do not persist it or use it as a stable identifier.</remarks>
    public override int GetHashCode() => ByteRouteHasher.Instance.Hash(_bytes.AsSpan());

    /// <summary>Tests two channel names for byte equality.</summary>
    public static bool operator ==(RespireChannel left, RespireChannel right) => left.Equals(right);

    /// <summary>Tests two channel names for byte inequality.</summary>
    public static bool operator !=(RespireChannel left, RespireChannel right) => !left.Equals(right);

    /// <summary>Decodes UTF-8 for display, replacing invalid bytes. Never use display text as identity.</summary>
    public override string ToString() => Utf8String.GetString(Bytes);

    internal static RespireChannel FromOwnedBytes(byte[] bytes) => new(bytes, SubscriptionKind.Channel);

    internal ReadOnlySpan<byte> Span => _bytes;

    internal RespireChannel WithKind(SubscriptionKind kind) => new(_bytes, kind);
    internal RespireValue AsValue() => new(Bytes);
}
