using System.Buffers.Text;
using System.Globalization;
using Respire.Commands;
using Respire.Protocol;

namespace Respire;

/// <summary>The unit used by Redis bitmap range offsets.</summary>
public enum BitIndexUnit
{
    /// <summary>Interpret offsets as byte indexes.</summary>
    Byte,

    /// <summary>Interpret offsets as bit indexes.</summary>
    Bit,
}

/// <summary>A Redis BITOP operation.</summary>
public enum BitOperation
{
    /// <summary>Bitwise AND of all source strings.</summary>
    And,

    /// <summary>Bitwise OR of all source strings.</summary>
    Or,

    /// <summary>Bitwise XOR of all source strings.</summary>
    Xor,

    /// <summary>Bitwise NOT of one source string.</summary>
    Not,

    /// <summary>Redis 8.2: bits set in the first source and absent from every later source.</summary>
    Diff,

    /// <summary>Redis 8.2: bits absent from the first source and set in any later source.</summary>
    Diff1,

    /// <summary>Redis 8.2: first source AND the OR of all later sources.</summary>
    AndOr,

    /// <summary>Redis 8.2: bits set in exactly one source.</summary>
    One,
}

/// <summary>Overflow behavior for subsequent Redis BITFIELD writes.</summary>
public enum BitFieldOverflow
{
    /// <summary>Wrap overflowing values modulo the field width.</summary>
    Wrap,

    /// <summary>Clamp overflowing values to the field's minimum or maximum.</summary>
    Saturate,

    /// <summary>Return null and leave the field unchanged on overflow.</summary>
    Fail,
}

/// <summary>A signed or unsigned integer encoding used by Redis BITFIELD.</summary>
public readonly record struct BitFieldEncoding
{
    private BitFieldEncoding(bool isSigned, int width)
    {
        IsSigned = isSigned;
        Width = width;
    }

    /// <summary>Whether the field is signed.</summary>
    public bool IsSigned { get; }

    /// <summary>The field width in bits.</summary>
    public int Width { get; }

    /// <summary>Creates a signed encoding from 1 through 64 bits.</summary>
    public static BitFieldEncoding Signed(int width)
    {
        if (width is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Signed width must be from 1 through 64.");
        }

        return new(true, width);
    }

    /// <summary>Creates an unsigned encoding from 1 through 63 bits.</summary>
    public static BitFieldEncoding Unsigned(int width)
    {
        if (width is < 1 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Unsigned width must be from 1 through 63.");
        }

        return new(false, width);
    }

    internal bool IsValid => Width > 0;
}

/// <summary>One GET, SET, INCRBY, or OVERFLOW operation inside Redis BITFIELD.</summary>
public readonly struct BitFieldOperation
{
    private BitFieldOperation(string command, string? encoding, string? offset, long value, BitFieldOverflow? overflow)
    {
        Command = command;
        Encoding = encoding;
        Offset = offset;
        Value = value;
        Overflow = overflow;
        StructuredEncoding = default;
        OffsetInFieldUnits = false;
    }

    private BitFieldOperation(BitFieldEncoding encoding, long offset, bool offsetInFieldUnits)
    {
        Command = "GET";
        Encoding = null;
        Offset = null;
        Value = offset;
        Overflow = null;
        StructuredEncoding = encoding;
        OffsetInFieldUnits = offsetInFieldUnits;
    }

    internal string Command { get; }
    internal string? Encoding { get; }
    internal string? Offset { get; }
    internal long Value { get; }
    internal BitFieldOverflow? Overflow { get; }
    internal BitFieldEncoding StructuredEncoding { get; }
    internal bool OffsetInFieldUnits { get; }
    internal bool HasStructuredArguments => StructuredEncoding.IsValid;
    internal int TokenCount => Command == "OVERFLOW" ? 2 : Command == "GET" ? 3 : 4;

    /// <summary>Reads a signed or unsigned field. Redis: BITFIELD GET.</summary>
    public static BitFieldOperation Get(string encoding, string offset)
        => ValueOperation("GET", encoding, offset, 0);

    /// <summary>Reads a typed signed or unsigned field. Redis: BITFIELD GET.</summary>
    public static BitFieldOperation Get(
        BitFieldEncoding encoding, long offset, bool offsetInFieldUnits = false)
    {
        if (!encoding.IsValid)
        {
            throw new ArgumentException("Use BitFieldEncoding.Signed or Unsigned.", nameof(encoding));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return new(encoding, offset, offsetInFieldUnits);
    }

    /// <summary>Writes a field and returns its previous value. Redis: BITFIELD SET.</summary>
    public static BitFieldOperation Set(string encoding, string offset, long value)
        => ValueOperation("SET", encoding, offset, value);

    /// <summary>Increments a field and returns its new value. Redis: BITFIELD INCRBY.</summary>
    public static BitFieldOperation Increment(string encoding, string offset, long by)
        => ValueOperation("INCRBY", encoding, offset, by);

    /// <summary>Changes overflow behavior for later writes. Redis: BITFIELD OVERFLOW.</summary>
    public static BitFieldOperation SetOverflow(BitFieldOverflow overflow)
    {
        if (!Enum.IsDefined(overflow))
        {
            throw new ArgumentOutOfRangeException(nameof(overflow));
        }

        return new("OVERFLOW", null, null, 0, overflow);
    }

    private static BitFieldOperation ValueOperation(string command, string encoding, string offset, long value)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoding);
        ArgumentException.ThrowIfNullOrEmpty(offset);

        if (!TryParseEncoding(encoding))
        {
            throw new ArgumentException("Encoding must be i1-i64 or u1-u63.", nameof(encoding));
        }

        ReadOnlySpan<char> numericOffset = offset;
        if (numericOffset[0] == '#')
        {
            numericOffset = numericOffset[1..];
        }

        if (!long.TryParse(numericOffset, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("Offset must be a non-negative integer, optionally prefixed with '#'.", nameof(offset));
        }

        return new(command, encoding, offset, value, null);
    }

    private static bool TryParseEncoding(string encoding)
    {
        if (encoding.Length is < 2 or > 3 || encoding[0] is not ('i' or 'u'))
        {
            return false;
        }

        if (!int.TryParse(encoding.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var width))
        {
            return false;
        }

        return encoding[0] == 'i' ? width is >= 1 and <= 64 : width is >= 1 and <= 63;
    }
}

/// <summary>Redis bitmap and bit-field commands.</summary>
public interface IBitmapCommands
{
    /// <summary>Returns the bit stored at an offset. Redis: GETBIT.</summary>
    ValueTask<bool> GetAsync(RespireKey key, long offset, CancellationToken cancellationToken = default);

    /// <summary>Sets the bit at an offset and returns its previous value. Redis: SETBIT.</summary>
#pragma warning disable CS0618 // Default preserves compatibility with existing interface implementations.
    ValueTask<bool> SetAsync(
        RespireKey key, long offset, bool value, CancellationToken cancellationToken = default)
        => GetAndSetAsync(key, offset, value, cancellationToken);
#pragma warning restore CS0618

    /// <summary>Sets the bit at an offset and returns its previous value. Redis: SETBIT.</summary>
    [Obsolete("Use SetAsync; SETBIT returns the previous bit.")]
    ValueTask<bool> GetAndSetAsync(
        RespireKey key, long offset, bool value, CancellationToken cancellationToken = default);

    /// <summary>Counts set bits across the whole value. Redis: BITCOUNT.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Counts set bits in an inclusive byte or bit range. Redis: BITCOUNT.</summary>
    ValueTask<long> CountAsync(
        RespireKey key, long start, long end, BitIndexUnit unit = BitIndexUnit.Byte,
        CancellationToken cancellationToken = default);

    /// <summary>The first offset holding <paramref name="value"/>, or null when none is found. Redis: BITPOS.</summary>
    ValueTask<long?> PositionAsync(
        RespireKey key, bool value, long? start = null, long? end = null,
        BitIndexUnit unit = BitIndexUnit.Byte, CancellationToken cancellationToken = default);

    /// <summary>Combines source bitmaps into a destination and returns its byte length. Redis: BITOP.</summary>
    ValueTask<long> OperateAsync(
        BitOperation operation, RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys);

    /// <summary>Combines source bitmaps into a destination and returns its byte length. Redis: BITOP.</summary>
    ValueTask<long> OperateAsync(
        BitOperation operation,
        RespireKey destination,
        ReadOnlySpan<RespireKey> sourceKeys,
        CancellationToken cancellationToken);

    /// <summary>Executes bit-field reads and writes. Redis: BITFIELD.</summary>
    ValueTask<long?[]> FieldAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> operations);

    /// <summary>Executes bit-field reads and writes. Redis: BITFIELD.</summary>
    ValueTask<long?[]> FieldAsync(
        RespireKey key, ReadOnlySpan<BitFieldOperation> operations, CancellationToken cancellationToken);

    /// <summary>Executes read-only bit-field GET operations. Redis: BITFIELD_RO.</summary>
    ValueTask<long?[]> FieldReadOnlyAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> gets);

    /// <summary>Executes read-only bit-field GET operations. Redis: BITFIELD_RO.</summary>
    ValueTask<long?[]> FieldReadOnlyAsync(
        RespireKey key, ReadOnlySpan<BitFieldOperation> gets, CancellationToken cancellationToken);
}

internal sealed class BitmapCommands(RespireClient client) : IBitmapCommands
{
    public ValueTask<bool> GetAsync(RespireKey key, long offset, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return client.FlagAsync(
            "GETBIT", new Cmd2(RespireCommands.Bitmap.GETBIT.Verb, client.Key(in key), offset), cancellationToken);
    }

    public ValueTask<bool> SetAsync(
        RespireKey key, long offset, bool value, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return client.FlagAsync(
            "SETBIT", new Cmd3(RespireCommands.Bitmap.SETBIT.Verb, client.Key(in key), offset, value), cancellationToken);
    }

    [Obsolete("Use SetAsync; SETBIT returns the previous bit.")]
    public ValueTask<bool> GetAndSetAsync(
        RespireKey key, long offset, bool value, CancellationToken cancellationToken = default)
        => SetAsync(key, offset, value, cancellationToken);

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            "BITCOUNT", new Cmd1(RespireCommands.Bitmap.BITCOUNT.Verb, client.Key(in key)), cancellationToken);

    public ValueTask<long> CountAsync(
        RespireKey key, long start, long end, BitIndexUnit unit = BitIndexUnit.Byte,
        CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            "BITCOUNT",
            new Cmd4(RespireCommands.Bitmap.BITCOUNT.Verb, client.Key(in key), start, end, Unit(unit)),
            cancellationToken);

    public ValueTask<long?> PositionAsync(
        RespireKey key, bool value, long? start = null, long? end = null,
        BitIndexUnit unit = BitIndexUnit.Byte, CancellationToken cancellationToken = default)
    {
        ValidatePosition(start, end, unit);
        return (start, end) switch
        {
            (null, _) => client.IntegerMinusOneOrNullAsync(
                "BITPOS", new Cmd2(RespireCommands.Bitmap.BITPOS.Verb, client.Key(in key), value), cancellationToken),
            ({ } from, null) => client.IntegerMinusOneOrNullAsync(
                "BITPOS", new Cmd3(RespireCommands.Bitmap.BITPOS.Verb, client.Key(in key), value, from), cancellationToken),
            ({ } from, { } to) => client.IntegerMinusOneOrNullAsync(
                "BITPOS", new Cmd5(
                    RespireCommands.Bitmap.BITPOS.Verb, client.Key(in key), value, from, to, Unit(unit)),
                cancellationToken),
        };
    }

    public ValueTask<long> OperateAsync(
        BitOperation operation, RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys)
        => OperateAsync(operation, destination, sourceKeys, CancellationToken.None);

    public ValueTask<long> OperateAsync(
        BitOperation operation,
        RespireKey destination,
        ReadOnlySpan<RespireKey> sourceKeys,
        CancellationToken cancellationToken)
    {
        ValidateOperate(operation, sourceKeys);
        return client.IntegerAsync(
            "BITOP",
            new BitOpCommand(
                RespireCommands.Bitmap.BITOP.Verb,
                Operation(operation),
                client.Key(in destination),
                client.MapKeys(sourceKeys)),
            cancellationToken);
    }

    public ValueTask<long?[]> FieldAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> operations)
        => FieldAsync(key, operations, CancellationToken.None);

    public ValueTask<long?[]> FieldAsync(
        RespireKey key, ReadOnlySpan<BitFieldOperation> operations, CancellationToken cancellationToken)
        => FieldCoreAsync(
            "BITFIELD", RespireCommands.Bitmap.BITFIELD, key, operations, readOnly: false, cancellationToken);

    public ValueTask<long?[]> FieldReadOnlyAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> gets)
        => FieldReadOnlyAsync(key, gets, CancellationToken.None);

    public ValueTask<long?[]> FieldReadOnlyAsync(
        RespireKey key, ReadOnlySpan<BitFieldOperation> gets, CancellationToken cancellationToken)
        => FieldCoreAsync(
            "BITFIELD_RO", RespireCommands.Bitmap.BITFIELD_RO, key, gets, readOnly: true, cancellationToken);

    private ValueTask<long?[]> FieldCoreAsync(
        string name,
        RespireCommand command,
        RespireKey key,
        ReadOnlySpan<BitFieldOperation> operations,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        ValidateFieldOperations(operations, readOnly);
        return client.NullableIntegerArrayAsync(
            name, new BitFieldCommand(command.Verb, client.Key(in key), operations.ToArray()), cancellationToken);
    }

    /// <summary>Shared with the deferred (batch/transaction) facet.</summary>
    internal static void ValidateFieldOperations(ReadOnlySpan<BitFieldOperation> operations, bool readOnly)
    {
        if (operations.IsEmpty)
        {
            throw new ArgumentException("At least one bit-field operation is required.", nameof(operations));
        }

        foreach (var operation in operations)
        {
            if (string.IsNullOrEmpty(operation.Command))
            {
                throw new ArgumentException("Operations must be created with BitFieldOperation factory methods.", nameof(operations));
            }

            if (readOnly && operation.Command != "GET")
            {
                throw new ArgumentException("BITFIELD_RO accepts GET operations only.", nameof(operations));
            }
        }
    }

    /// <summary>Shared with the deferred (batch/transaction) facet.</summary>
    internal static void ValidateOperate(BitOperation operation, ReadOnlySpan<RespireKey> sourceKeys)
    {
        if (sourceKeys.IsEmpty)
        {
            throw new ArgumentException("At least one source key is required.", nameof(sourceKeys));
        }

        if (operation == BitOperation.Not && sourceKeys.Length != 1)
        {
            throw new ArgumentException("BITOP NOT requires exactly one source key.", nameof(sourceKeys));
        }
    }

    /// <summary>Shared with the deferred (batch/transaction) facet.</summary>
    internal static void ValidatePosition(long? start, long? end, BitIndexUnit unit)
    {
        if (end.HasValue && !start.HasValue)
        {
            throw new ArgumentException("End requires start.", nameof(end));
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit));
        }

        if (start.HasValue && !end.HasValue && unit != BitIndexUnit.Byte)
        {
            throw new ArgumentException("Bit indexing requires end because Redis places BYTE|BIT after end.", nameof(unit));
        }
    }

    internal static string Unit(BitIndexUnit unit) => unit switch
    {
        BitIndexUnit.Byte => "BYTE",
        BitIndexUnit.Bit => "BIT",
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    internal static string Operation(BitOperation operation) => operation switch
    {
        BitOperation.And => "AND",
        BitOperation.Or => "OR",
        BitOperation.Xor => "XOR",
        BitOperation.Not => "NOT",
        BitOperation.Diff => "DIFF",
        BitOperation.Diff1 => "DIFF1",
        BitOperation.AndOr => "ANDOR",
        BitOperation.One => "ONE",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}

internal readonly struct BitOpCommand(
    Verb verb,
    RespireValue operation,
    RespireValue destination,
    RespireValue[] sourceKeys) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot) => destination.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 2 + sourceKeys.Length);
        writer.WriteRaw(verb.Bulk);
        operation.WriteTo(ref writer);
        destination.WriteTo(ref writer);
        foreach (var sourceKey in sourceKeys)
        {
            sourceKey.WriteTo(ref writer);
        }
    }
}

internal readonly struct BitFieldCommand(Verb verb, RespireValue key, BitFieldOperation[] operations) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        var tokenCount = 1;
        foreach (var operation in operations)
        {
            tokenCount += operation.TokenCount;
        }

        writer.WriteArrayHeader(verb.Tokens + tokenCount);
        writer.WriteRaw(verb.Bulk);
        key.WriteTo(ref writer);
        Span<byte> encodingBuffer = stackalloc byte[3];
        Span<byte> offsetBuffer = stackalloc byte[21];
        foreach (var operation in operations)
        {
            writer.WriteBulkString(operation.Command);
            if (operation.Command == "OVERFLOW")
            {
                writer.WriteBulkString(operation.Overflow switch
                {
                    BitFieldOverflow.Wrap => "WRAP",
                    BitFieldOverflow.Saturate => "SAT",
                    BitFieldOverflow.Fail => "FAIL",
                    _ => throw new InvalidOperationException("Missing overflow mode."),
                });
                continue;
            }

            if (operation.HasStructuredArguments)
            {
                encodingBuffer[0] = operation.StructuredEncoding.IsSigned ? (byte)'i' : (byte)'u';
                Utf8Formatter.TryFormat(
                    operation.StructuredEncoding.Width, encodingBuffer[1..], out var encodingLength);
                writer.WriteBulkString(encodingBuffer[..(encodingLength + 1)]);

                if (operation.OffsetInFieldUnits)
                {
                    offsetBuffer[0] = (byte)'#';
                    Utf8Formatter.TryFormat(operation.Value, offsetBuffer[1..], out var offsetLength);
                    writer.WriteBulkString(offsetBuffer[..(offsetLength + 1)]);
                }
                else
                {
                    writer.WriteBulkInteger(operation.Value);
                }
            }
            else
            {
                writer.WriteBulkString(operation.Encoding!);
                writer.WriteBulkString(operation.Offset!);
            }

            if (operation.Command != "GET")
            {
                writer.WriteBulkInteger(operation.Value);
            }
        }
    }
}
