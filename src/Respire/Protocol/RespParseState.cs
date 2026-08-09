using System.Runtime.CompilerServices;
using Respire.Networking;

namespace Respire.Protocol;

internal readonly record struct RespDirectFillRequest(RespDataType Type, int PayloadLength);

/// <summary>
/// Reusable connection parser that retains completed aggregate children and decoded bulk
/// headers across receives. Consumed bytes may be compacted immediately by the caller.
/// </summary>
internal sealed class RespParseState(int directFillThreshold) : IDisposable
{
    private AggregateFrame[] _frames = new AggregateFrame[4];
    private int _depth;
    private bool _hasPendingBulk;
    private RespDataType _pendingBulkType;
    private int _pendingBulkLength;

    internal bool IsIdle => _depth == 0 && !_hasPendingBulk;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespParseStatus TryParse(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        out RespValue value,
        out RespDirectFillRequest directFill)
    {
        value = default;
        directFill = default;

        if (IsIdle && pos < buffer.Length)
        {
            var typeByte = buffer[pos];
            if (typeByte is (byte)'$' or (byte)'=' or (byte)'!')
            {
                return TryParseInitialBulk(buffer, ref pos, typeByte, out value, out directFill);
            }

            var cursor = pos;
            var initialStatus = RespParser.TryParseValue(buffer, ref cursor, out value);
            if (initialStatus != RespParseStatus.NeedMoreData
                || typeByte is not ((byte)'*' or (byte)'~' or (byte)'>' or (byte)'%' or (byte)'|'))
            {
                if (initialStatus == RespParseStatus.Done)
                {
                    pos = cursor;
                }

                return initialStatus;
            }
        }

        return TryParseResumable(buffer, ref pos, out value, out directFill);
    }

    internal RespParseStatus TryParseResumable(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        out RespValue value,
        out RespDirectFillRequest directFill)
    {
        value = default;
        directFill = default;

        while (true)
        {
            if (_hasPendingBulk)
            {
                var status = TryCompletePendingBulk(buffer, ref pos, out var bulk);
                if (status != RespParseStatus.Done)
                {
                    return status;
                }

                if (AcceptValue(in bulk, out value))
                {
                    return RespParseStatus.Done;
                }

                continue;
            }

            if (pos >= buffer.Length)
            {
                return RespParseStatus.NeedMoreData;
            }

            var typeByte = buffer[pos];
            if (typeByte is (byte)'$' or (byte)'=' or (byte)'!')
            {
                var status = ParseBulkHeader(buffer, ref pos, typeByte, out var immediate, out directFill);
                if (status == RespParseStatus.Done)
                {
                    if (AcceptValue(in immediate, out value))
                    {
                        return RespParseStatus.Done;
                    }

                    continue;
                }

                if (status == RespParseStatus.NeedMoreData && _hasPendingBulk)
                {
                    continue;
                }

                return status;
            }

            if (typeByte is (byte)'*' or (byte)'~' or (byte)'>' or (byte)'%' or (byte)'|')
            {
                var status = ParseAggregateHeader(buffer, ref pos, typeByte, out var immediate, out var discard);
                if (status != RespParseStatus.Done)
                {
                    return status;
                }

                if (discard)
                {
                    immediate.Dispose();
                    continue;
                }

                if (immediate.Type != default && AcceptValue(in immediate, out value))
                {
                    return RespParseStatus.Done;
                }

                continue;
            }

            var cursor = pos;
            var scalarStatus = RespParser.TryParseCore(buffer, ref cursor, out var scalar);
            if (scalarStatus != RespParseStatus.Done)
            {
                return scalarStatus;
            }

            pos = cursor;
            if (AcceptValue(in scalar, out value))
            {
                return RespParseStatus.Done;
            }
        }
    }

    public bool SupplyDirectFill(in RespValue filledValue, out RespValue value)
        => AcceptValue(in filledValue, out value);

    internal void PrepareBulk(RespDataType type, int payloadLength)
    {
        _pendingBulkType = type;
        _pendingBulkLength = payloadLength;
        _hasPendingBulk = true;
    }

    private RespParseStatus TryParseInitialBulk(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        byte typeByte,
        out RespValue value,
        out RespDirectFillRequest directFill)
    {
        value = default;
        directFill = default;
        var headerEnd = pos + 1;
        if (!RespParser.TryReadLine(buffer, ref headerEnd, out var lengthLine))
        {
            return RespParseStatus.NeedMoreData;
        }

        if (!RespParser.TryParseInt64(lengthLine, out var declaredLength))
        {
            return RespParseStatus.InvalidData;
        }

        var type = typeByte switch
        {
            (byte)'$' => RespDataType.BulkString,
            (byte)'=' => RespDataType.VerbatimString,
            _ => RespDataType.BulkError,
        };
        if (declaredLength >= directFillThreshold)
        {
            if (declaredLength > int.MaxValue - 2)
            {
                return RespParseStatus.InvalidData;
            }

            pos = headerEnd;
            directFill = new RespDirectFillRequest(type, (int)declaredLength);
            return RespParseStatus.NeedDirectFill;
        }

        var cursor = pos;
        var status = RespParser.TryParseBulkValue(
            buffer, ref cursor, type, declaredLength, headerEnd, out value);
        if (status != RespParseStatus.NeedMoreData)
        {
            if (status == RespParseStatus.Done)
            {
                pos = cursor;
            }

            return status;
        }

        var length = (int)declaredLength;
        pos = headerEnd;
        _pendingBulkType = type;
        _pendingBulkLength = length;
        _hasPendingBulk = true;
        return RespParseStatus.NeedMoreData;
    }

    private RespParseStatus ParseBulkHeader(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        byte typeByte,
        out RespValue value,
        out RespDirectFillRequest directFill)
    {
        value = default;
        directFill = default;
        var cursor = pos + 1;
        if (!RespParser.TryReadLine(buffer, ref cursor, out var lengthLine))
        {
            return RespParseStatus.NeedMoreData;
        }

        if (!RespParser.TryParseInt64(lengthLine, out var declaredLength))
        {
            return RespParseStatus.InvalidData;
        }

        pos = cursor;
        if (declaredLength == -1)
        {
            value = RespValue.Null;
            return RespParseStatus.Done;
        }

        if (declaredLength < 0 || declaredLength > int.MaxValue - 2)
        {
            return RespParseStatus.InvalidData;
        }

        var type = typeByte switch
        {
            (byte)'$' => RespDataType.BulkString,
            (byte)'=' => RespDataType.VerbatimString,
            _ => RespDataType.BulkError,
        };
        var length = (int)declaredLength;
        if (length >= directFillThreshold)
        {
            directFill = new RespDirectFillRequest(type, length);
            return RespParseStatus.NeedDirectFill;
        }

        _pendingBulkType = type;
        _pendingBulkLength = length;
        _hasPendingBulk = true;
        return RespParseStatus.NeedMoreData;
    }

    private RespParseStatus TryCompletePendingBulk(
        ReadOnlySpan<byte> buffer, ref int pos, out RespValue value)
    {
        value = default;
        if (buffer.Length - pos < _pendingBulkLength + 2)
        {
            return RespParseStatus.NeedMoreData;
        }

        if (buffer[pos + _pendingBulkLength] != RespConstants.CarriageReturn
            || buffer[pos + _pendingBulkLength + 1] != RespConstants.LineFeed)
        {
            return RespParseStatus.InvalidData;
        }

        value = RespParser.CopyToPooled(_pendingBulkType, buffer.Slice(pos, _pendingBulkLength));
        pos += _pendingBulkLength + 2;
        _hasPendingBulk = false;
        return RespParseStatus.Done;
    }

    private RespParseStatus ParseAggregateHeader(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        byte typeByte,
        out RespValue value,
        out bool discard)
    {
        value = default;
        discard = typeByte == (byte)'|';
        var cursor = pos + 1;
        if (!RespParser.TryReadLine(buffer, ref cursor, out var countLine))
        {
            return RespParseStatus.NeedMoreData;
        }

        if (!RespParser.TryParseInt64(countLine, out var declaredCount))
        {
            return RespParseStatus.InvalidData;
        }

        pos = cursor;
        if (declaredCount == -1)
        {
            value = RespValue.Null;
            return RespParseStatus.Done;
        }

        var pairCount = typeByte is (byte)'%' or (byte)'|';
        if (declaredCount < 0 || declaredCount > (pairCount ? int.MaxValue / 2 : int.MaxValue))
        {
            return RespParseStatus.InvalidData;
        }

        var elementCount = pairCount ? declaredCount * 2 : declaredCount;

        var type = typeByte switch
        {
            (byte)'~' => RespDataType.Set,
            (byte)'>' => RespDataType.Push,
            (byte)'%' or (byte)'|' => RespDataType.Map,
            _ => RespDataType.Array,
        };
        var count = (int)elementCount;
        if (count == 0)
        {
            value = RespValue.PooledAggregate(type, [], 0);
            return RespParseStatus.Done;
        }

        PushFrame(type, count, discard);
        discard = false;
        return RespParseStatus.Done;
    }

    private void PushFrame(RespDataType type, int count, bool discard)
    {
        if (_depth == _frames.Length)
        {
            Array.Resize(ref _frames, _frames.Length * 2);
        }

        _frames[_depth++] = new AggregateFrame(type, RespirePools.ValueArrays.Rent(count), count, discard);
    }

    private bool AcceptValue(in RespValue accepted, out RespValue value)
    {
        var current = accepted;
        while (_depth > 0)
        {
            ref var frame = ref _frames[_depth - 1];
            frame.Elements[frame.Index++] = current;
            if (frame.Index < frame.Count)
            {
                value = default;
                return false;
            }

            current = RespValue.PooledAggregate(frame.Type, frame.Elements, frame.Count);
            var discard = frame.Discard;
            frame = default;
            _depth--;
            if (discard)
            {
                current.Dispose();
                value = default;
                return false;
            }
        }

        value = current;
        return true;
    }

    public void Dispose()
    {
        for (var i = 0; i < _depth; i++)
        {
            ref var frame = ref _frames[i];
            for (var j = 0; j < frame.Index; j++)
            {
                frame.Elements[j].Dispose();
            }

            RespirePools.ValueArrays.Return(frame.Elements, clearArray: true);
            frame = default;
        }

        _depth = 0;
        _hasPendingBulk = false;
    }

    private struct AggregateFrame(RespDataType type, RespValue[] elements, int count, bool discard)
    {
        public RespDataType Type = type;
        public RespValue[] Elements = elements;
        public int Count = count;
        public int Index;
        public bool Discard = discard;
    }
}
