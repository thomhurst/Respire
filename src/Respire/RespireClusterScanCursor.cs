using System.Diagnostics.CodeAnalysis;
using System.Text;
using Respire.Internal;

namespace Respire;

/// <summary>An immutable, serializable checkpoint for a Cluster key scan.</summary>
/// <remarks>Persist the next cursor only after processing its page. Cursors contain scan filters,
/// node identities and slot progress, but no credentials or client connections. Duplicate keys
/// are possible. A cursor is not a snapshot of the database and is not an authenticated token.</remarks>
public sealed class RespireClusterScanCursor
{
    private const int FormatMagic = 0x31435352; // RSC1
    private const int BinaryPrefixFormatMagic = 0x32435352; // RSC2
    private const int ExtendedFormatMagic = 0x33435352; // RSC3
    private const int MaximumEncodedLength = 6 * 1024 * 1024;
    internal ClusterScanState? State { get; }
    internal RespireClusterScanCursor(ClusterScanState? state)
    {
        State = state;
        CompletedSlotCount = state?.Completed.Count(static done => done) ?? 0;
    }

    /// <summary>The checkpoint before the first page.</summary>
    public static RespireClusterScanCursor Start { get; } = new(null);
    /// <summary>Whether every slot has completed a validated scan.</summary>
    public bool IsComplete => CompletedSlotCount == ClusterHash.SlotCount;
    /// <summary>The number of slots whose scan has completed, from zero to 16384.</summary>
    public int CompletedSlotCount { get; }

    /// <summary>Serializes this checkpoint as an opaque, versioned Base64 string.</summary>
    public override string ToString()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var format = FormatMagic;
        if (State?.BinaryPrefix is not null) format = BinaryPrefixFormatMagic;
        if (State?.Database is not null || State?.ValkeyCursor is not null) format = ExtendedFormatMagic;
        writer.Write(format);
        writer.Write(State is not null);
        if (State is { } state)
        {
            WriteFilter(writer, state.Match, format);
            WriteFilter(writer, state.Type, format);
            WriteFilter(writer, state.Prefix, format);
            if (format == ExtendedFormatMagic)
                writer.Write(state.BinaryPrefix?.Length ?? 0);
            if (state.BinaryPrefix is { } prefix)
            {
                if (format != ExtendedFormatMagic) writer.Write(prefix.Length);
                writer.Write(prefix);
            }
            var identities = state.Owners.Distinct(StringComparer.Ordinal).ToArray();
            writer.Write(identities.Length);
            foreach (var identity in identities) writer.Write(identity);
            var indices = identities.Select((identity, index) => (identity, index))
                .ToDictionary(static entry => entry.identity, static entry => entry.index, StringComparer.Ordinal);
            // Run-length encoding keeps the common contiguous slot layout compact.
            for (var slot = 0; slot < ClusterHash.SlotCount;)
            {
                var end = slot + 1;
                while (end < ClusterHash.SlotCount && state.Owners[end] == state.Owners[slot]) end++;
                writer.Write((ushort)(end - slot));
                writer.Write((ushort)indices[state.Owners[slot]]);
                slot = end;
            }
            WriteBits(writer, state.Completed);
            WriteText(writer, state.ActiveNode);
            WriteText(writer, state.RunId);
            writer.Write(state.Epoch);
            writer.Write(state.Cursor);
            WriteBits(writer, state.PassSlots);
            if (format == ExtendedFormatMagic)
            {
                writer.Write(state.Database ?? 0);
                WriteText(writer, state.ValkeyCursor);
            }
        }
        var encoded = Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
        if (encoded.Length > MaximumEncodedLength) throw new InvalidOperationException("Cluster scan cursor exceeds its serialization limit.");
        return encoded;
    }

    /// <summary>Parses a checkpoint produced by <see cref="ToString"/>.</summary>
    /// <exception cref="FormatException">The checkpoint is malformed, too large or has an unsupported version.</exception>
    public static RespireClusterScanCursor Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumEncodedLength) throw new FormatException("Cluster scan cursor is too large.");
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(value), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
            var format = reader.ReadInt32();
            if (format is not FormatMagic and not BinaryPrefixFormatMagic and not ExtendedFormatMagic)
                throw new FormatException("Unsupported Cluster scan cursor version.");
            if (!reader.ReadBoolean())
            {
                if (format != FormatMagic) throw new FormatException("Extended Cluster scan cursors require scan state.");
                if (stream.Position != stream.Length) throw new FormatException("Unexpected cursor data.");
                return Start;
            }
            var state = new ClusterScanState(ReadFilter(reader, format), ReadFilter(reader, format), ReadFilter(reader, format));
            if (format is BinaryPrefixFormatMagic or ExtendedFormatMagic)
            {
                var prefixLength = reader.ReadInt32();
                if (prefixLength < 0 || format == BinaryPrefixFormatMagic && prefixLength == 0
                    || prefixLength > 0 && state.Prefix is not null || prefixLength > stream.Length - stream.Position)
                    throw new FormatException("Invalid binary Cluster scan prefix.");
                if (prefixLength > 0) state.BinaryPrefix = reader.ReadBytes(prefixLength);
            }
            var count = reader.ReadInt32();
            if (count is < 1 or > ClusterHash.SlotCount) throw new FormatException("Invalid cursor node count.");
            var identities = new string[count];
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                identities[index] = reader.ReadString();
                if (identities[index].Length is < 1 or > 128 || !unique.Add(identities[index]))
                    throw new FormatException("Invalid cursor node identity.");
            }
            for (var slot = 0; slot < ClusterHash.SlotCount;)
            {
                var length = reader.ReadUInt16();
                var owner = reader.ReadUInt16();
                if (length == 0 || length > ClusterHash.SlotCount - slot || owner >= count)
                    throw new FormatException("Invalid cursor slot range.");
                Array.Fill(state.Owners, identities[owner], slot, length);
                slot += length;
            }
            ReadBits(reader, state.Completed);
            state.ActiveNode = ReadText(reader);
            state.RunId = ReadText(reader);
            // The unsigned epoch uses the same eight bytes as every previously valid
            // nonnegative checkpoint, and now also preserves the upper half of the range.
            state.Epoch = reader.ReadUInt64();
            state.Cursor = reader.ReadUInt64();
            ReadBits(reader, state.PassSlots);
            if (format == ExtendedFormatMagic)
            {
                state.Database = reader.ReadInt32();
                state.ValkeyCursor = ReadText(reader);
                if (state.Database < 0 || state.ValkeyCursor is { } opaque
                    && (opaque.Length == 0 || opaque == "0" || state.Cursor != 0
                        || state.ActiveNode is null || !state.PassSlots[ClusterHash.GetSlot(opaque)]))
                    throw new FormatException("Inconsistent Valkey Cluster scan cursor state.");
            }
            if (stream.Position != stream.Length
                || state.ActiveNode is not null && (!unique.Contains(state.ActiveNode) || string.IsNullOrEmpty(state.RunId))
                || state.ActiveNode is null && (state.Cursor != 0 || state.PassSlots.Any(static bit => bit)))
                throw new FormatException("Inconsistent Cluster scan cursor state.");
            for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
                if (state.PassSlots[slot] && (state.Completed[slot] || state.Owners[slot] != state.ActiveNode))
                    throw new FormatException("Inconsistent Cluster scan slot progress.");
            return new(state);
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
        {
            throw new FormatException("Invalid Cluster scan cursor.", error);
        }
    }

    /// <summary>Tries to parse an opaque Cluster scan checkpoint.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out RespireClusterScanCursor? cursor)
    {
        cursor = null;
        if (value is null) return false;
        try { cursor = Parse(value); return true; }
        catch (FormatException) { return false; }
    }

    private static void WriteText(BinaryWriter writer, string? text)
    {
        writer.Write(text is not null);
        if (text is not null) writer.Write(text);
    }
    private static string? ReadText(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
    private static void WriteFilter(BinaryWriter writer, string? text, int format)
    {
        if (format != ExtendedFormatMagic) { WriteText(writer, text); return; }
        writer.Write(text is not null);
        if (text is null) return;
        // Filters and text prefixes have UTF-16 identity, including unpaired surrogates.
        // Encoding them as UTF-8 would silently change a persisted checkpoint's filters.
        writer.Write(text.Length);
        foreach (var character in text) writer.Write((ushort)character);
    }
    private static string? ReadFilter(BinaryReader reader, int format)
    {
        if (format != ExtendedFormatMagic) return ReadText(reader);
        if (!reader.ReadBoolean()) return null;
        var length = reader.ReadInt32();
        if (length < 0 || length > (reader.BaseStream.Length - reader.BaseStream.Position) / 2)
            throw new FormatException("Invalid Cluster scan filter length.");
        var characters = new char[length];
        for (var index = 0; index < characters.Length; index++) characters[index] = (char)reader.ReadUInt16();
        return new string(characters);
    }
    private static void WriteBits(BinaryWriter writer, bool[] bits)
    {
        for (var index = 0; index < bits.Length; index += 8)
        {
            byte packed = 0;
            for (var bit = 0; bit < 8; bit++) if (bits[index + bit]) packed |= (byte)(1 << bit);
            writer.Write(packed);
        }
    }
    private static void ReadBits(BinaryReader reader, bool[] bits)
    {
        for (var index = 0; index < bits.Length; index += 8)
        {
            var packed = reader.ReadByte();
            for (var bit = 0; bit < 8; bit++) bits[index + bit] = (packed & (1 << bit)) != 0;
        }
    }
}

/// <summary>A caller-owned page of keys and its next immutable Cluster scan checkpoint.</summary>
/// <remarks>An empty page does not imply completion. Inspect Cursor.IsComplete. COUNT is a server
/// work hint, not a page-size limit. Keys uses the same string and key-prefix semantics as ScanAsync.</remarks>
public sealed record RespireClusterScanPage(RespireClusterScanCursor Cursor,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] Keys)
{
    /// <summary>Whether every remaining slot is migrating or importing, so no scan pass can complete.</summary>
    /// <remarks>The page contains no keys. Delay before requesting the next page; cancellation or
    /// an application deadline can bound a transition that never settles.</remarks>
    public bool WaitingOnMigration { get; init; }
}

internal sealed class ClusterScanState(string? match, string? type, string? prefix)
{
    internal string? Match { get; } = match;
    internal string? Type { get; } = type;
    internal string? Prefix { get; } = prefix;
    internal byte[]? BinaryPrefix;
    internal string[] Owners { get; } = new string[ClusterHash.SlotCount];
    internal bool[] Completed { get; } = new bool[ClusterHash.SlotCount];
    internal bool[] PassSlots { get; } = new bool[ClusterHash.SlotCount];
    internal string? ActiveNode;
    internal string? RunId;
    internal ulong Epoch;
    internal ulong Cursor;
    internal string? ValkeyCursor;
    internal int? Database;

    internal bool MatchesKeyPrefix(KeyPrefix? prefix)
        => Prefix == prefix?.Text
            && BinaryPrefix.AsSpan().SequenceEqual(prefix is { Text: null } ? prefix.Bytes : null);

    internal ClusterScanState Copy()
    {
        var copy = new ClusterScanState(Match, Type, Prefix)
        {
            ActiveNode = ActiveNode, RunId = RunId, Epoch = Epoch, Cursor = Cursor, BinaryPrefix = BinaryPrefix,
            ValkeyCursor = ValkeyCursor,
            Database = Database,
        };
        Owners.CopyTo(copy.Owners, 0);
        Completed.CopyTo(copy.Completed, 0);
        PassSlots.CopyTo(copy.PassSlots, 0);
        return copy;
    }

    internal void ResetPass()
    {
        ActiveNode = null;
        RunId = null;
        Epoch = 0;
        Cursor = 0;
        ValkeyCursor = null;
        Array.Clear(PassSlots);
    }
}
