using Respire.Commands;
using Respire.Protocol;
using System.Globalization;

namespace Respire;

public enum BitIndexUnit
{
    Byte,
    Bit,
}

public enum BitOperation
{
    And,
    Or,
    Xor,
    Not,
    Diff,
    Diff1,
    AndOr,
    One,
}

public enum BitFieldOverflow
{
    Wrap,
    Saturate,
    Fail,
}

public readonly struct BitFieldOperation
{
    private BitFieldOperation(string command, string? encoding, string? offset, long value, BitFieldOverflow? overflow)
    {
        Command = command;
        Encoding = encoding;
        Offset = offset;
        Value = value;
        Overflow = overflow;
    }

    internal string Command { get; }
    internal string? Encoding { get; }
    internal string? Offset { get; }
    internal long Value { get; }
    internal BitFieldOverflow? Overflow { get; }
    internal int TokenCount => Command == "OVERFLOW" ? 2 : Command == "GET" ? 3 : 4;

    public static BitFieldOperation Get(string encoding, string offset)
        => ValueOperation("GET", encoding, offset, 0);

    public static BitFieldOperation Set(string encoding, string offset, long value)
        => ValueOperation("SET", encoding, offset, value);

    public static BitFieldOperation Increment(string encoding, string offset, long by)
        => ValueOperation("INCRBY", encoding, offset, by);

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

public interface IBitmapCommands
{
    ValueTask<bool> GetAsync(RespireKey key, long offset, CancellationToken cancellationToken = default);
    ValueTask<bool> SetAsync(RespireKey key, long offset, bool value, CancellationToken cancellationToken = default);
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);
    ValueTask<long> CountAsync(
        RespireKey key, long start, long end, BitIndexUnit unit = BitIndexUnit.Byte,
        CancellationToken cancellationToken = default);
    ValueTask<long> PositionAsync(
        RespireKey key, bool value, long? start = null, long? end = null,
        BitIndexUnit unit = BitIndexUnit.Byte, CancellationToken cancellationToken = default);
    ValueTask<long> OperateAsync(
        BitOperation operation, RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys);
    ValueTask<long?[]> FieldAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> operations);
    ValueTask<long?[]> FieldReadOnlyAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> gets);
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

    public ValueTask<long> PositionAsync(
        RespireKey key, bool value, long? start = null, long? end = null,
        BitIndexUnit unit = BitIndexUnit.Byte, CancellationToken cancellationToken = default)
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

        return (start, end) switch
        {
            (null, _) => client.IntegerAsync(
                "BITPOS", new Cmd2(RespireCommands.Bitmap.BITPOS.Verb, client.Key(in key), value), cancellationToken),
            ({ } from, null) => client.IntegerAsync(
                "BITPOS", new Cmd3(RespireCommands.Bitmap.BITPOS.Verb, client.Key(in key), value, from), cancellationToken),
            ({ } from, { } to) => client.IntegerAsync(
                "BITPOS", new Cmd5(
                    RespireCommands.Bitmap.BITPOS.Verb, client.Key(in key), value, from, to, Unit(unit)),
                cancellationToken),
        };
    }

    public ValueTask<long> OperateAsync(
        BitOperation operation, RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys)
    {
        if (sourceKeys.IsEmpty)
        {
            throw new ArgumentException("At least one source key is required.", nameof(sourceKeys));
        }

        if (operation == BitOperation.Not && sourceKeys.Length != 1)
        {
            throw new ArgumentException("BITOP NOT requires exactly one source key.", nameof(sourceKeys));
        }

        return client.IntegerAsync(
            "BITOP",
            new BitOpCommand(
                RespireCommands.Bitmap.BITOP.Verb,
                Operation(operation),
                client.Key(in destination),
                client.MapKeys(sourceKeys)),
            CancellationToken.None);
    }

    public ValueTask<long?[]> FieldAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> operations)
        => FieldCoreAsync("BITFIELD", RespireCommands.Bitmap.BITFIELD, key, operations, readOnly: false);

    public ValueTask<long?[]> FieldReadOnlyAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> gets)
        => FieldCoreAsync("BITFIELD_RO", RespireCommands.Bitmap.BITFIELD_RO, key, gets, readOnly: true);

    private ValueTask<long?[]> FieldCoreAsync(
        string name, RespireCommand command, RespireKey key, ReadOnlySpan<BitFieldOperation> operations, bool readOnly)
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

        return client.NullableIntegerArrayAsync(
            name, new BitFieldCommand(command.Verb, client.Key(in key), operations.ToArray()), CancellationToken.None);
    }

    private static string Unit(BitIndexUnit unit) => unit switch
    {
        BitIndexUnit.Byte => "BYTE",
        BitIndexUnit.Bit => "BIT",
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private static string Operation(BitOperation operation) => operation switch
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

            writer.WriteBulkString(operation.Encoding!);
            writer.WriteBulkString(operation.Offset!);
            if (operation.Command != "GET")
            {
                writer.WriteBulkInteger(operation.Value);
            }
        }
    }
}
