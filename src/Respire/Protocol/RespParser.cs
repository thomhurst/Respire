using System.Buffers.Text;
using System.Runtime.CompilerServices;
using Respire.Networking;

namespace Respire.Protocol;

/// <summary>
/// Incremental, allocation-conscious RESP2/RESP3 response parser over a contiguous receive
/// buffer. Payloads are copied exactly once, off the wire into pooled storage owned by the
/// resulting <see cref="RespValue"/>.
/// </summary>
/// <remarks>
/// The stateless API is restartable: on <see cref="RespParseStatus.NeedMoreData"/> the caller
/// receives more bytes and calls again from the same start position; any partially built
/// aggregate storage is released before returning. The connection uses a reusable
/// <see cref="RespParseState"/> to retain aggregate progress and direct-fill large bulk values
/// at any nesting depth.
/// </remarks>
public enum RespParseStatus : byte
{
    Done,
    NeedMoreData,
    InvalidData,
    NeedDirectFill,
}

public static class RespParser
{
    /// <summary>
    /// Attempts to parse one complete RESP value starting at <paramref name="pos"/>.
    /// On <see cref="RespParseStatus.Done"/>, <paramref name="pos"/> is advanced past the value.
    /// On any other status, <paramref name="pos"/> is unchanged and no storage is retained.
    /// </summary>
    public static RespParseStatus TryParseValue(ReadOnlySpan<byte> buffer, ref int pos, out RespValue value)
    {
        value = default;
        var cursor = pos;

        // RESP3 attribute frames ("|") annotate the reply that follows; parse and discard them.
        while (true)
        {
            if (cursor >= buffer.Length)
            {
                return RespParseStatus.NeedMoreData;
            }

            if (buffer[cursor] != (byte)'|')
            {
                break;
            }

            var attrStatus = TryParseAggregate(buffer, ref cursor, RespDataType.Map, pairCount: true, out var attribute);
            if (attrStatus != RespParseStatus.Done)
            {
                return attrStatus;
            }

            attribute.Dispose();
        }

        var status = TryParseCore(buffer, ref cursor, out value);
        if (status == RespParseStatus.Done)
        {
            pos = cursor;
        }

        return status;
    }

    /// <summary>
    /// Peeks a bulk-string-family header ("$&lt;len&gt;\r\n", "=&lt;len&gt;\r\n", "!&lt;len&gt;\r\n") at
    /// <paramref name="pos"/> without consuming it.
    /// </summary>
    internal static bool TryPeekBulkHeader(
        ReadOnlySpan<byte> buffer, int pos, out RespDataType type, out long payloadLength, out int headerEnd)
    {
        type = default;
        payloadLength = 0;
        headerEnd = 0;

        if (pos >= buffer.Length)
        {
            return false;
        }

        var typeByte = buffer[pos];
        if (typeByte is not ((byte)'$' or (byte)'=' or (byte)'!'))
        {
            return false;
        }

        var cursor = pos + 1;
        if (!TryReadLine(buffer, ref cursor, out var line) || !TryParseInt64(line, out payloadLength))
        {
            return false;
        }

        type = typeByte switch
        {
            (byte)'$' => RespDataType.BulkString,
            (byte)'=' => RespDataType.VerbatimString,
            _ => RespDataType.BulkError,
        };
        headerEnd = cursor;
        return true;
    }

    /// <summary>
    /// Parses a bulk-string-family value using a header already decoded by
    /// <see cref="TryPeekBulkHeader"/>. This avoids scanning and parsing the length twice in
    /// the connection receive path.
    /// </summary>
    internal static RespParseStatus TryParseBulkValue(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        RespDataType type,
        long payloadLength,
        int headerEnd,
        out RespValue value)
    {
        value = default;

        if (payloadLength == -1)
        {
            value = RespValue.Null;
            pos = headerEnd;
            return RespParseStatus.Done;
        }

        if (payloadLength < 0 || payloadLength > int.MaxValue - 2)
        {
            return RespParseStatus.InvalidData;
        }

        var length = (int)payloadLength;
        var total = length + 2;
        if (buffer.Length - headerEnd < total)
        {
            return RespParseStatus.NeedMoreData;
        }

        if (buffer[headerEnd + length] != RespConstants.CarriageReturn
            || buffer[headerEnd + length + 1] != RespConstants.LineFeed)
        {
            return RespParseStatus.InvalidData;
        }

        value = CopyToPooled(type, buffer.Slice(headerEnd, length));
        pos = headerEnd + total;
        return RespParseStatus.Done;
    }

    internal static RespParseStatus TryParseCore(ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value)
    {
        value = default;
        var typeByte = buffer[cursor];

        switch (typeByte)
        {
            case (byte)'+':
                return TryParseLineString(buffer, ref cursor, RespDataType.SimpleString, out value);
            case (byte)'-':
                return TryParseLineString(buffer, ref cursor, RespDataType.Error, out value);
            case (byte)'(':
                return TryParseLineString(buffer, ref cursor, RespDataType.BigNumber, out value);
            case (byte)':':
                return TryParseInteger(buffer, ref cursor, out value);
            case (byte)'#':
                return TryParseBoolean(buffer, ref cursor, out value);
            case (byte)',':
                return TryParseDouble(buffer, ref cursor, out value);
            case (byte)'_':
                return TryParseNull(buffer, ref cursor, out value);
            case (byte)'$':
                return TryParseBulk(buffer, ref cursor, RespDataType.BulkString, out value);
            case (byte)'=':
                return TryParseBulk(buffer, ref cursor, RespDataType.VerbatimString, out value);
            case (byte)'!':
                return TryParseBulk(buffer, ref cursor, RespDataType.BulkError, out value);
            case (byte)'*':
                return TryParseAggregate(buffer, ref cursor, RespDataType.Array, pairCount: false, out value);
            case (byte)'~':
                return TryParseAggregate(buffer, ref cursor, RespDataType.Set, pairCount: false, out value);
            case (byte)'>':
                return TryParseAggregate(buffer, ref cursor, RespDataType.Push, pairCount: false, out value);
            case (byte)'%':
                return TryParseAggregate(buffer, ref cursor, RespDataType.Map, pairCount: true, out value);
            default:
                return RespParseStatus.InvalidData;
        }
    }

    private static RespParseStatus TryParseLineString(
        ReadOnlySpan<byte> buffer, ref int cursor, RespDataType type, out RespValue value)
    {
        value = default;
        var pos = cursor + 1;
        if (!TryReadLine(buffer, ref pos, out var line))
        {
            return RespParseStatus.NeedMoreData;
        }

        value = CopyToPooled(type, line);
        cursor = pos;
        return RespParseStatus.Done;
    }

    private static RespParseStatus TryParseInteger(ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value)
    {
        value = default;
        var pos = cursor + 1;
        if (!TryReadLine(buffer, ref pos, out var line))
        {
            return RespParseStatus.NeedMoreData;
        }

        if (!TryParseInt64(line, out var integer))
        {
            return RespParseStatus.InvalidData;
        }

        value = RespValue.Integer(integer);
        cursor = pos;
        return RespParseStatus.Done;
    }

    private static RespParseStatus TryParseBoolean(ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value)
    {
        value = default;
        var pos = cursor + 1;
        if (!TryReadLine(buffer, ref pos, out var line))
        {
            return RespParseStatus.NeedMoreData;
        }

        if (line.Length != 1 || line[0] is not ((byte)'t' or (byte)'f'))
        {
            return RespParseStatus.InvalidData;
        }

        value = line[0] == (byte)'t' ? RespValue.True : RespValue.False;
        cursor = pos;
        return RespParseStatus.Done;
    }

    private static RespParseStatus TryParseDouble(ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value)
    {
        value = default;
        var pos = cursor + 1;
        if (!TryReadLine(buffer, ref pos, out var line))
        {
            return RespParseStatus.NeedMoreData;
        }

        double result;
        if (line.SequenceEqual("inf"u8))
        {
            result = double.PositiveInfinity;
        }
        else if (line.SequenceEqual("-inf"u8))
        {
            result = double.NegativeInfinity;
        }
        else if (line.SequenceEqual("nan"u8))
        {
            result = double.NaN;
        }
        else if (!Utf8Parser.TryParse(line, out result, out var consumed) || consumed != line.Length)
        {
            return RespParseStatus.InvalidData;
        }

        value = RespValue.Double(result);
        cursor = pos;
        return RespParseStatus.Done;
    }

    private static RespParseStatus TryParseNull(ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value)
    {
        value = default;
        var pos = cursor + 1;
        if (!TryReadLine(buffer, ref pos, out _))
        {
            return RespParseStatus.NeedMoreData;
        }

        value = RespValue.Null;
        cursor = pos;
        return RespParseStatus.Done;
    }

    private static RespParseStatus TryParseBulk(
        ReadOnlySpan<byte> buffer, ref int cursor, RespDataType type, out RespValue value)
    {
        value = default;
        var pos = cursor + 1;
        if (!TryReadLine(buffer, ref pos, out var lengthLine))
        {
            return RespParseStatus.NeedMoreData;
        }

        if (!TryParseInt64(lengthLine, out var length))
        {
            return RespParseStatus.InvalidData;
        }

        return TryParseBulkValue(buffer, ref cursor, type, length, pos, out value);
    }

    private static RespParseStatus TryParseAggregate(
        ReadOnlySpan<byte> buffer, ref int cursor, RespDataType type, bool pairCount, out RespValue value)
    {
        value = default;
        var pos = cursor + 1;
        if (!TryReadLine(buffer, ref pos, out var countLine))
        {
            return RespParseStatus.NeedMoreData;
        }

        if (!TryParseInt64(countLine, out var declaredCount))
        {
            return RespParseStatus.InvalidData;
        }

        if (declaredCount == -1)
        {
            value = RespValue.Null;
            cursor = pos;
            return RespParseStatus.Done;
        }

        var elementCount = pairCount ? declaredCount * 2 : declaredCount;
        if (elementCount < 0 || elementCount > int.MaxValue)
        {
            return RespParseStatus.InvalidData;
        }

        var count = (int)elementCount;
        if (count == 0)
        {
            value = RespValue.PooledAggregate(type, [], 0);
            cursor = pos;
            return RespParseStatus.Done;
        }

        var elements = RespirePools.ValueArrays.Rent(count);
        for (var i = 0; i < count; i++)
        {
            RespParseStatus status;
            if (pos >= buffer.Length)
            {
                status = RespParseStatus.NeedMoreData;
            }
            else
            {
                status = TryParseCore(buffer, ref pos, out elements[i]);
            }

            if (status != RespParseStatus.Done)
            {
                for (var j = 0; j < i; j++)
                {
                    elements[j].Dispose();
                }

                RespirePools.ValueArrays.Return(elements, clearArray: true);
                return status;
            }
        }

        value = RespValue.PooledAggregate(type, elements, count);
        cursor = pos;
        return RespParseStatus.Done;
    }

    private static readonly ReadOnlyMemory<byte> InternedOk = "OK"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> InternedPong = "PONG"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> InternedQueued = "QUEUED"u8.ToArray();

    internal static RespValue CopyToPooled(RespDataType type, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return RespValue.PooledString(type, [], 0);
        }

        // The constant replies that dominate write-heavy traffic (+OK, +PONG, +QUEUED) are
        // interned: no pooled rent on this thread + return on the caller's thread per reply.
        if (type == RespDataType.SimpleString)
        {
            if (payload.SequenceEqual("OK"u8))
            {
                return RespValue.SimpleString(InternedOk);
            }

            if (payload.SequenceEqual("PONG"u8))
            {
                return RespValue.SimpleString(InternedPong);
            }

            if (payload.SequenceEqual("QUEUED"u8))
            {
                return RespValue.SimpleString(InternedQueued);
            }
        }

        var array = RespirePools.ResponsePayloads.Rent(payload.Length);
        payload.CopyTo(array);
        return RespValue.PooledString(type, array, payload.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryReadLine(ReadOnlySpan<byte> buffer, ref int pos, out ReadOnlySpan<byte> line)
    {
        var index = buffer[pos..].IndexOf("\r\n"u8);
        if (index < 0)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(pos, index);
        pos += index + 2;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseInt64(ReadOnlySpan<byte> line, out long value)
        => Utf8Parser.TryParse(line, out value, out var consumed) && consumed == line.Length;
}
