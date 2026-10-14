namespace Respire;

/// <summary>A command was rejected locally because its keys use different Redis Cluster slots.</summary>
/// <remarks>No command was sent or queued. Use the same nonempty hash tag, such as {tenant},
/// in every key. Slots are calculated after applying the client's key prefix.</remarks>
public sealed class RespireCrossSlotException : RespireException
{
    /// <summary>The command rejected before I/O or enqueueing.</summary>
    public string CommandName { get; }

    /// <summary>The slot established by an earlier key.</summary>
    public int ExpectedSlot { get; }

    /// <summary>The different slot of the conflicting key.</summary>
    public int ConflictingSlot { get; }

    /// <summary>The conflicting physical key as hexadecimal bytes, or null by default.</summary>
    /// <remarks>Only captured when RespireOptions.IncludeCrossSlotKeyContext is enabled.
    /// Hexadecimal is reversible and must be treated as sensitive data.</remarks>
    public string? ConflictingKeyHex { get; }

    /// <summary>Creates a local cross-slot error without key context.</summary>
    public RespireCrossSlotException(string commandName, int expectedSlot, int conflictingSlot)
        : base($"CROSSSLOT Keys in request don't hash to the same slot: {commandName} uses slots {expectedSlot} and {conflictingSlot}. Use the same nonempty hash tag (for example {{tenant}}) in every key, including any key prefix.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSlot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expectedSlot, 16383);
        ArgumentOutOfRangeException.ThrowIfNegative(conflictingSlot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(conflictingSlot, 16383);
        IsCommandNotSubmitted = true;
        CommandName = commandName;
        ExpectedSlot = expectedSlot;
        ConflictingSlot = conflictingSlot;
    }

    private RespireCrossSlotException(string commandName, int expectedSlot, int conflictingSlot, string? keyHex)
        : this(commandName, expectedSlot, conflictingSlot) => ConflictingKeyHex = keyHex;

    internal static RespireCrossSlotException Create(
        bool includeKeyContext, string commandName, int expectedSlot, int conflictingSlot, RespireValue key)
        => new(commandName, expectedSlot, conflictingSlot,
            includeKeyContext ? Convert.ToHexString(key.AsKey().ToBytes()) : null);
}
