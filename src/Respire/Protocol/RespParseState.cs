using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Networking;

namespace Respire.Protocol;

internal readonly record struct RespDirectFillRequest(RespireDataType Type, int PayloadLength);

/// <summary>
/// Reusable connection parser that retains completed aggregate children and decoded bulk
/// headers across receives. Consumed bytes may be compacted immediately by the caller.
/// </summary>
internal sealed class RespParseState(int directFillThreshold, bool stopAfterAttributes = false) : IDisposable
{
    private AggregateFrame[] _frames = new AggregateFrame[4];
    private int _depth;
    private bool _hasPendingBulk;
    private RespireDataType _pendingBulkType;
    private int _pendingBulkLength;

#if DEBUG
    // Per-parser progress evidence for friend tests; absent from Release layout and work.
    internal int ResumedScalarCountForTests { get; private set; }

    private void RecordResumedScalarForTests()
    {
        ResumedScalarCountForTests++;
    }
#endif

    internal bool IsIdle => _depth == 0 && !_hasPendingBulk;

    // Only top-level attributes yield to the connection's bulk fast path;
    // attributes inside an unfinished aggregate remain part of that aggregate.
    private bool ShouldYieldAfterAttribute => stopAfterAttributes && IsIdle;

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
            var initialStatus = RespParser.TryParseValue(buffer, ref cursor, out value, this);
            if (initialStatus != RespParseStatus.NeedMoreData
                || typeByte is not ((byte)'*' or (byte)'~' or (byte)'>' or (byte)'%' or (byte)'|'))
            {
                if (initialStatus == RespParseStatus.Done)
                {
                    pos = cursor;
                }

                return initialStatus;
            }

            if (!IsIdle)
                pos = cursor;
        }

        var status = TryParseResumable(buffer, ref pos, out value, out directFill);
        Debug.Assert(stopAfterAttributes || status != RespParseStatus.SkippedAttribute,
            "Only parsers that opt in may yield after an attribute.");
        return status;
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

                if (ShouldYieldAfterAttribute)
                {
                    return RespParseStatus.SkippedAttribute;
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

                    if (ShouldYieldAfterAttribute)
                    {
                        return RespParseStatus.SkippedAttribute;
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
                    if (ShouldYieldAfterAttribute)
                    {
                        return RespParseStatus.SkippedAttribute;
                    }

                    continue;
                }

                if (immediate.Type != default && AcceptValue(in immediate, out value))
                {
                    return RespParseStatus.Done;
                }

                if (ShouldYieldAfterAttribute)
                {
                    return RespParseStatus.SkippedAttribute;
                }

                continue;
            }

            var cursor = pos;
            // Aggregate markers were handled above; scalar parsing needs no storage budget.
            var scalarStatus = RespParser.TryParseScalar(buffer, ref cursor, out var scalar);
            if (scalarStatus != RespParseStatus.Done)
            {
                return scalarStatus;
            }

#if DEBUG
            RecordResumedScalarForTests();
#endif
            pos = cursor;
            if (AcceptValue(in scalar, out value))
            {
                return RespParseStatus.Done;
            }

            if (ShouldYieldAfterAttribute)
            {
                return RespParseStatus.SkippedAttribute;
            }
        }
    }

    public bool SupplyDirectFill(in RespValue filledValue, out RespValue value)
        => AcceptValue(in filledValue, out value);

    internal void PrepareBulk(RespireDataType type, int payloadLength)
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
        var headerStatus = RespParser.TryReadInt64Line(buffer, ref headerEnd, out var declaredLength);
        if (headerStatus != RespParseStatus.Done)
        {
            return headerStatus;
        }

        var type = typeByte switch
        {
            (byte)'$' => RespireDataType.BulkString,
            (byte)'=' => RespireDataType.VerbatimString,
            _ => RespireDataType.BulkError,
        };
        if (declaredLength >= directFillThreshold && declaredLength > buffer.Length - headerEnd - 2)
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
        var headerStatus = RespParser.TryReadInt64Line(buffer, ref cursor, out var declaredLength);
        if (headerStatus != RespParseStatus.Done)
        {
            return headerStatus;
        }

        pos = cursor;
        if (declaredLength == -1)
        {
            value = RespValue.NullFromWire((RespireDataType)typeByte);
            return RespParseStatus.Done;
        }

        if (declaredLength < 0 || declaredLength > int.MaxValue - 2)
        {
            return RespParseStatus.InvalidData;
        }

        var type = typeByte switch
        {
            (byte)'$' => RespireDataType.BulkString,
            (byte)'=' => RespireDataType.VerbatimString,
            _ => RespireDataType.BulkError,
        };
        var length = (int)declaredLength;
        if (length >= directFillThreshold && buffer.Length - pos < length + 2)
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
        var headerStatus = RespParser.TryReadInt64Line(buffer, ref cursor, out var declaredCount);
        if (headerStatus != RespParseStatus.Done)
        {
            return headerStatus;
        }

        if (declaredCount == -1)
        {
            pos = cursor;
            value = RespValue.NullFromWire((RespireDataType)typeByte);
            return RespParseStatus.Done;
        }

        var pairCount = typeByte is (byte)'%' or (byte)'|';
        if (!RespAggregateStorage.TryValidate(declaredCount, pairCount, _depth, out var count))
        {
            return RespParseStatus.InvalidData;
        }

        pos = cursor;

        var type = typeByte switch
        {
            (byte)'~' => RespireDataType.Set,
            (byte)'>' => RespireDataType.Push,
            (byte)'%' or (byte)'|' => RespireDataType.Map,
            _ => RespireDataType.Array,
        };
        if (count == 0)
        {
            value = RespValue.PooledAggregate(type, [], 0);
            return RespParseStatus.Done;
        }

        PushFrame(type, count, discard);
        discard = false;
        return RespParseStatus.Done;
    }

    private void PushFrame(RespireDataType type, int count, bool discard)
    {
        if (_depth == _frames.Length)
        {
            Array.Resize(ref _frames, _frames.Length * 2);
        }

        _frames[_depth++] = new AggregateFrame(type, [], count, discard);
    }

    internal void AdoptPartialAggregate(
        RespireDataType type, int count, int depth, Span<RespValue> completed, ReadOnlySpan<byte> buffer)
    {
        while (depth >= _frames.Length)
            Array.Resize(ref _frames, _frames.Length * 2);

        Debug.Assert(_frames[depth].Elements is null);
        Debug.Assert(_depth == 0
            || (depth + 1 < _depth && _frames[depth + 1].Elements is not null));
        // Stateless recursion unwinds from the deepest incomplete aggregate first.
        // Retain only incremental capacity, rather than its speculative full-count rent.
        _frames[depth] = new AggregateFrame(type, [], count, discard: false);
        _depth = Math.Max(_depth, depth + 1);
        ref var frame = ref _frames[depth];
        for (var i = 0; i < completed.Length; i++)
        {
            if (frame.Index == frame.Elements.Length)
                RespAggregateStorage.Grow(ref frame.Elements, count);
            frame.Elements[frame.Index] = completed[i].CopyDeferredPayloadsIndividually(buffer);
            frame.Index++;
            completed[i] = default;
        }
    }

    private bool AcceptValue(in RespValue accepted, out RespValue value)
    {
        var current = accepted;
        while (_depth > 0)
        {
            ref var frame = ref _frames[_depth - 1];
            if (frame.Index == frame.Elements.Length)
                RespAggregateStorage.Grow(ref frame.Elements, frame.Count);
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

            if (frame.Elements is { Length: > 0 })
            {
                System.Array.Clear(frame.Elements, 0, frame.Index);
                RespirePools.ValueArrays.Return(frame.Elements);
            }
            frame = default;
        }

        _depth = 0;
        _hasPendingBulk = false;
    }

    private struct AggregateFrame(RespireDataType type, RespValue[] elements, int count, bool discard)
    {
        public RespireDataType Type = type;
        public RespValue[] Elements = elements;
        public int Count = count;
        public int Index;
        public bool Discard = discard;
    }
}
