using System.Buffers.Text;
using System.Diagnostics;
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
internal enum RespParseStatus : byte
{
    Done,
    NeedMoreData,
    InvalidData,
    NeedDirectFill,
    /// <summary>
    /// A top-level attribute was consumed; resume at the following reply.
    /// Only the resumable parser with stopAfterAttributes enabled returns this status.
    /// </summary>
    SkippedAttribute,
}

internal static class RespParser
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsAttributeStart(byte value) => value == (byte)'|';

    /// <summary>
    /// Attempts to parse one complete RESP value starting at <paramref name="pos"/>.
    /// On <see cref="RespParseStatus.Done"/>, <paramref name="pos"/> is advanced past the value.
    /// On any other status, <paramref name="pos"/> is unchanged and no storage is retained.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespParseStatus TryParseValue(ReadOnlySpan<byte> buffer, ref int pos, out RespValue value)
        => TryParseValue(buffer, ref pos, out value, progressOwner: null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static RespParseStatus TryParseValue(
        ReadOnlySpan<byte> buffer, ref int pos, out RespValue value, RespParseState? progressOwner)
    {
        value = default;
        if (pos >= buffer.Length)
            return RespParseStatus.NeedMoreData;

        // Scalar replies need neither aggregate depth nor a storage budget.
        if (buffer[pos] is not ((byte)'*' or (byte)'~' or (byte)'>' or (byte)'%' or (byte)'|'))
        {
            var cursor = pos;
            var status = TryParseScalar(buffer, ref cursor, out value);
            if (status == RespParseStatus.Done)
            {
                value.AssertMaterialized();
                pos = cursor;
            }
            return status;
        }

        // Even the shortest RESP value takes three bytes. Share this rent budget across
        // nested aggregates and attributes, rather than trusting each declared count.
        var remainingElements = (buffer.Length - pos) / 3;
        var deferredPayloadBytes = 0;
        var aggregateStatus = TryParseValue(buffer, ref pos, out value,
            new ParseContext(0, ref remainingElements, ref deferredPayloadBytes), progressOwner, out var start);
        if (aggregateStatus == RespParseStatus.Done)
        {
            if (deferredPayloadBytes != 0)
                value = value.CopyDeferredPayloads(buffer, start, pos - start, deferredPayloadBytes);
            value.AssertMaterialized();
        }
        return aggregateStatus;
    }

    /// <summary>
    /// Copies branch depth while sharing the root's rent budget and deferred payload byte count.
    /// </summary>
    private readonly ref struct ParseContext
    {
        private readonly ref int _remainingElements;
        private readonly ref int _deferredPayloadBytes;
        public int Depth { get; }
        // Constructed contexts defer payloads; ImmediateCopy is only for scalar/bulk
        // entry points and has no aggregate budget for ForChildren/TryReserve.
        public static ParseContext ImmediateCopy => default;
        public bool DeferPayloads { get; }
        public int DeferredPayloadBytes { get => _deferredPayloadBytes; set => _deferredPayloadBytes = value; }

        public ParseContext(int depth, ref int remainingElements, ref int deferredPayloadBytes)
        {
            Depth = depth;
            _remainingElements = ref remainingElements;
            _deferredPayloadBytes = ref deferredPayloadBytes;
            DeferPayloads = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseContext ForChildren()
        {
            Debug.Assert(DeferPayloads, "ImmediateCopy has no aggregate budget.");
            return new(Depth + 1, ref _remainingElements, ref _deferredPayloadBytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReserve(int count)
        {
            Debug.Assert(DeferPayloads, "ImmediateCopy has no aggregate budget.");
            if (count > _remainingElements)
                return false;

            // Each child, including a nested aggregate header, needs three wire bytes.
            _remainingElements -= count;
            return true;
        }
    }

    private static RespParseStatus TryParseValue(
        ReadOnlySpan<byte> buffer, ref int pos, out RespValue value, ParseContext context,
        RespParseState? progressOwner, out int valueStart)
    {
        value = default;
        var cursor = pos;
        valueStart = cursor;

        // RESP3 attribute frames ("|") annotate the reply that follows; parse and discard them.
        while (true)
        {
            if (cursor >= buffer.Length)
            {
                return RespParseStatus.NeedMoreData;
            }

            if (!IsAttributeStart(buffer[cursor]))
            {
                break;
            }

            var priorPayloadBytes = context.DeferredPayloadBytes;
            var attrStatus = TryParseAggregate(buffer, ref cursor, RespireDataType.Map, pairCount: true,
                out var attribute, context, progressOwner: null);
            if (attrStatus != RespParseStatus.Done)
            {
                return attrStatus;
            }

            attribute.Dispose();
            context.DeferredPayloadBytes = priorPayloadBytes;
        }

        valueStart = cursor;
        var status = TryParseCore(buffer, ref cursor, out value, context, progressOwner);
        // Completed attributes may advance to valueStart without adopting a frame.
        // Consuming an incomplete value itself requires owned aggregate progress.
        Debug.Assert(status != RespParseStatus.NeedMoreData || cursor == valueStart
            || progressOwner is { IsIdle: false },
            "An incomplete value can advance the cursor only after adopting progress.");
        if (status == RespParseStatus.Done
            || (status == RespParseStatus.NeedMoreData && progressOwner is not null))
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
        ReadOnlySpan<byte> buffer, int pos, out RespireDataType type, out long payloadLength, out int headerEnd)
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
        if (TryReadInt64Line(buffer, ref cursor, out payloadLength) != RespParseStatus.Done)
        {
            return false;
        }

        type = typeByte switch
        {
            (byte)'$' => RespireDataType.BulkString,
            (byte)'=' => RespireDataType.VerbatimString,
            _ => RespireDataType.BulkError,
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
        RespireDataType type,
        long payloadLength,
        int headerEnd,
        out RespValue value)
        => TryParseBulkValue(buffer, ref pos, type, payloadLength, headerEnd, out value, ParseContext.ImmediateCopy);

    private static RespParseStatus TryParseBulkValue(
        ReadOnlySpan<byte> buffer, ref int pos, RespireDataType type, long payloadLength, int headerEnd,
        out RespValue value, ParseContext context)
    {
        value = default;

        if (payloadLength == -1)
        {
            value = RespValue.NullFromWire(type);
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

        value = CreatePayload(type, buffer, headerEnd, length, context);
        pos = headerEnd + total;
        return RespParseStatus.Done;
    }

    internal static RespParseStatus TryParseScalar(ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value)
        => TryParseScalar(buffer, ref cursor, out value, ParseContext.ImmediateCopy);

    private static RespParseStatus TryParseScalar(ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value, ParseContext context)
    {
        value = default;
        var typeByte = buffer[cursor];

        switch (typeByte)
        {
            case (byte)'+':
                return TryParseLineString(buffer, ref cursor, RespireDataType.SimpleString, out value, context);
            case (byte)'-':
                return TryParseLineString(buffer, ref cursor, RespireDataType.Error, out value, context);
            case (byte)'(':
                return TryParseLineString(buffer, ref cursor, RespireDataType.BigNumber, out value, context);
            case (byte)':':
                return TryParseInteger(buffer, ref cursor, out value);
            case (byte)'#':
                return TryParseBoolean(buffer, ref cursor, out value);
            case (byte)',':
                return TryParseDouble(buffer, ref cursor, out value);
            case (byte)'_':
                return TryParseNull(buffer, ref cursor, out value);
            case (byte)'$':
                return TryParseBulk(buffer, ref cursor, RespireDataType.BulkString, out value, context);
            case (byte)'=':
                return TryParseBulk(buffer, ref cursor, RespireDataType.VerbatimString, out value, context);
            case (byte)'!':
                return TryParseBulk(buffer, ref cursor, RespireDataType.BulkError, out value, context);
            default:
                return RespParseStatus.InvalidData;
        }
    }

    private static RespParseStatus TryParseCore(
        ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value, ParseContext context,
        RespParseState? progressOwner)
    {
        switch (buffer[cursor])
        {
            case (byte)'*':
                return TryParseAggregate(buffer, ref cursor, RespireDataType.Array, pairCount: false, out value, context, progressOwner);
            case (byte)'~':
                return TryParseAggregate(buffer, ref cursor, RespireDataType.Set, pairCount: false, out value, context, progressOwner);
            case (byte)'>':
                return TryParseAggregate(buffer, ref cursor, RespireDataType.Push, pairCount: false, out value, context, progressOwner);
            case (byte)'%':
                return TryParseAggregate(buffer, ref cursor, RespireDataType.Map, pairCount: true, out value, context, progressOwner);
            case (byte)'|':
                return TryParseValue(buffer, ref cursor, out value, context, progressOwner, out _);
            default:
                return TryParseScalar(buffer, ref cursor, out value, context);
        }
    }

    private static RespParseStatus TryParseLineString(
        ReadOnlySpan<byte> buffer, ref int cursor, RespireDataType type, out RespValue value, ParseContext context)
    {
        value = default;
        var pos = cursor + 1;
        if (!TryReadLine(buffer, ref pos, out var line))
        {
            return RespParseStatus.NeedMoreData;
        }

        value = CreatePayload(type, buffer, cursor + 1, line.Length, context);
        cursor = pos;
        return RespParseStatus.Done;
    }

    private static RespParseStatus TryParseInteger(ReadOnlySpan<byte> buffer, ref int cursor, out RespValue value)
    {
        value = default;
        var pos = cursor + 1;
        var status = TryReadInt64Line(buffer, ref pos, out var integer);
        if (status != RespParseStatus.Done)
        {
            return status;
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

        // Finite doubles dominate; check the RESP3 special spellings only after a failed parse.
        if (!Utf8Parser.TryParse(line, out double result, out var consumed) || consumed != line.Length)
        {
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
            else
            {
                return RespParseStatus.InvalidData;
            }
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
        ReadOnlySpan<byte> buffer, ref int cursor, RespireDataType type, out RespValue value, ParseContext context)
    {
        value = default;
        var pos = cursor + 1;
        var status = TryReadInt64Line(buffer, ref pos, out var length);
        if (status != RespParseStatus.Done)
        {
            return status;
        }

        return TryParseBulkValue(buffer, ref cursor, type, length, pos, out value, context);
    }

    private static RespParseStatus TryParseAggregate(
        ReadOnlySpan<byte> buffer, ref int cursor, RespireDataType type, bool pairCount, out RespValue value,
        ParseContext context, RespParseState? progressOwner)
    {
        value = default;
        var pos = cursor + 1;
        var headerStatus = TryReadInt64Line(buffer, ref pos, out var declaredCount);
        if (headerStatus != RespParseStatus.Done)
        {
            return headerStatus;
        }

        if (declaredCount == -1)
        {
            value = RespValue.NullFromWire(type);
            cursor = pos;
            return RespParseStatus.Done;
        }

        if (!RespAggregateStorage.TryValidate(declaredCount, pairCount, context.Depth, out var count))
        {
            return RespParseStatus.InvalidData;
        }

        if (!context.TryReserve(count))
        {
            return RespParseStatus.NeedMoreData;
        }

        if (count == 0)
        {
            value = RespValue.PooledAggregate(type, [], 0);
            cursor = pos;
            return RespParseStatus.Done;
        }

        var elements = RespirePools.ValueArrays.Rent(count);
        var childContext = context.ForChildren();
        for (var i = 0; i < count; i++)
        {
#if DEBUG
            var childStart = pos;
#endif
            RespParseStatus status;
            if (pos >= buffer.Length)
            {
                status = RespParseStatus.NeedMoreData;
            }
            else
            {
                status = TryParseCore(buffer, ref pos, out elements[i], childContext, progressOwner);
            }

            if (status != RespParseStatus.Done)
            {
#if DEBUG
                // Attributes may consume completed metadata. TryParseValue checks
                // the following value against its own start after that metadata.
                Debug.Assert(status != RespParseStatus.NeedMoreData || pos == childStart
                    || progressOwner is { IsIdle: false }
                    || buffer[childStart] == (byte)'|',
                    "An incomplete child can consume value bytes only after adopting progress.");
#endif
                ReleaseIncompleteAggregate(buffer, ref cursor, pos, type, count, context.Depth,
                    elements, i, status, progressOwner);
                return status;
            }
        }

        value = RespValue.PooledAggregate(type, elements, count);
        cursor = pos;
        return RespParseStatus.Done;
    }

    // Keep exception handling and progress transfer off the fully buffered loop.
    // The owner travels only through aggregate recursion, never scalar/payload contexts.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReleaseIncompleteAggregate(
        ReadOnlySpan<byte> buffer, ref int cursor, int pos, RespireDataType type, int count, int depth,
        RespValue[] elements, int completedCount, RespParseStatus status, RespParseState? progressOwner)
    {
        try
        {
            // Nested frames transfer first. Even an empty parent must join that chain.
            if (status == RespParseStatus.NeedMoreData
                && progressOwner is { } owner && (completedCount != 0 || !owner.IsIdle))
            {
                owner.AdoptPartialAggregate(type, count, depth, elements.AsSpan(0, completedCount), buffer);
                cursor = pos;
            }
        }
        finally
        {
            // Adoption clears moved slots. Only unmoved children remain ours.
            for (var i = 0; i < completedCount; i++)
                elements[i].Dispose();
            System.Array.Clear(elements, 0, completedCount);
            RespirePools.ValueArrays.Return(elements);
        }
    }

    private static readonly ReadOnlyMemory<byte> InternedOk = "OK"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> InternedPong = "PONG"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> InternedQueued = "QUEUED"u8.ToArray();

    private static RespValue CreatePayload(RespireDataType type, ReadOnlySpan<byte> buffer, int offset, int length, ParseContext context)
    {
        if (!context.DeferPayloads || length == 0)
            return CopyToPooled(type, buffer.Slice(offset, length));
        if (type == RespireDataType.SimpleString && TryGetInternedSimpleString(buffer.Slice(offset, length), out var interned))
            return RespValue.SimpleString(interned);
        // Empty and interned strings are already materialized, so positive bytes also
        // indicate whether the root has any deferred payloads to copy.
        context.DeferredPayloadBytes += length;
        return RespValue.DeferredString(type, offset, length);
    }

    internal static RespValue CopyToPooled(RespireDataType type, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return RespValue.PooledString(type, [], 0);
        }

        // The constant replies that dominate write-heavy traffic (+OK, +PONG, +QUEUED) are
        // interned: no pooled rent on this thread + return on the caller's thread per reply.
        if (type == RespireDataType.SimpleString && TryGetInternedSimpleString(payload, out var interned))
            return RespValue.SimpleString(interned);

        var array = RespirePools.ResponsePayloads.Rent(payload.Length);
        payload.CopyTo(array);
        return RespValue.PooledString(type, array, payload.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetInternedSimpleString(ReadOnlySpan<byte> payload, out ReadOnlyMemory<byte> interned)
    {
        if (payload.SequenceEqual("OK"u8)) interned = InternedOk;
        else if (payload.SequenceEqual("PONG"u8)) interned = InternedPong;
        else if (payload.SequenceEqual("QUEUED"u8)) interned = InternedQueued;
        else
        {
            interned = default;
            return false;
        }
        return true;
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

    // 18 decimal digits cannot overflow a long in either sign.
    private const int MaxFastInt64Digits = 18;

    /// <summary>
    /// Reads a CRLF-terminated integer header line (lengths, counts, ":" integers) starting at
    /// <paramref name="pos"/>. Advances <paramref name="pos"/> past the CRLF only on
    /// <see cref="RespParseStatus.Done"/>. Results match <see cref="TryReadLine"/> followed by
    /// <see cref="TryParseInt64"/>: NeedMoreData when no CRLF is buffered, InvalidData when the
    /// line is not a whole Int64.
    /// </summary>
    /// <remarks>
    /// The common shape, an optional '-' and 1-18 digits directly followed by CRLF, is
    /// accumulated in one pass. Such a line contains no earlier CRLF and parses to the same
    /// value, so anything else (a '+', more digits, other bytes, a partial line) defers to the
    /// scanning path unchanged.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static RespParseStatus TryReadInt64Line(ReadOnlySpan<byte> buffer, ref int pos, out long value)
    {
        var i = pos;
        var negative = (uint)i < (uint)buffer.Length && buffer[i] == (byte)'-';
        if (negative)
        {
            i++;
        }

        var digitsStart = i;
        var digitsLimit = Math.Min(buffer.Length, digitsStart + MaxFastInt64Digits);
        ulong magnitude = 0;
        while (i < digitsLimit)
        {
            var digit = (uint)(buffer[i] - (byte)'0');
            if (digit > 9)
            {
                break;
            }

            magnitude = magnitude * 10 + digit;
            i++;
        }

        if (i != digitsStart
            && (uint)(i + 1) < (uint)buffer.Length
            && buffer[i] == RespConstants.CarriageReturn
            && buffer[i + 1] == RespConstants.LineFeed)
        {
            value = negative ? -(long)magnitude : (long)magnitude;
            pos = i + 2;
            return RespParseStatus.Done;
        }

        return TryReadInt64LineSlow(buffer, ref pos, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RespParseStatus TryReadInt64LineSlow(ReadOnlySpan<byte> buffer, ref int pos, out long value)
    {
        var cursor = pos;
        if (!TryReadLine(buffer, ref cursor, out var line))
        {
            value = 0;
            return RespParseStatus.NeedMoreData;
        }

        if (!TryParseInt64(line, out value))
        {
            return RespParseStatus.InvalidData;
        }

        pos = cursor;
        return RespParseStatus.Done;
    }
}
