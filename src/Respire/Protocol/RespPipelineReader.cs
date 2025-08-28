using System.Buffers;
using System.Runtime.CompilerServices;

namespace Respire.Protocol;

/// <summary>
/// Zero-allocation RESP protocol reader using System.IO.Pipelines
/// Parses Redis responses directly from pipeline buffers without copying data
/// </summary>
public ref struct RespPipelineReader
{
    private ReadOnlySequence<byte> _sequence;
    private SequencePosition _position;
    private SequencePosition _consumed;
    private SequencePosition _examined;
    
    public RespPipelineReader(ReadOnlySequence<byte> sequence)
    {
        _sequence = sequence;
        _position = sequence.Start;
        _consumed = sequence.Start;
        _examined = sequence.Start;
    }
    
    public SequencePosition Consumed => _consumed;
    public SequencePosition Examined => _examined;
    public bool IsAtEnd => _sequence.IsEmpty || _position.Equals(_sequence.End);
    
    /// <summary>
    /// Attempts to read a complete RESP value from the pipeline
    /// </summary>
    /// <param name="value">The parsed RESP value</param>
    /// <returns>True if a complete value was read, false if more data is needed</returns>
    public bool TryReadValue(out RespireValue value)
    {
        value = default;
        
        if (IsAtEnd)
            return false;
        
        if (!TryPeekByte(out var typeByte))
            return false;
        
        return typeByte switch
        {
            (byte)'+' => TryReadSimpleString(out value),
            (byte)'-' => TryReadError(out value),
            (byte)':' => TryReadInteger(out value),
            (byte)'$' => TryReadBulkString(out value),
            (byte)'*' => TryReadArray(out value),
            (byte)'#' => TryReadBoolean(out value),
            _ => false
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadSimpleString(out RespireValue value)
    {
        value = default;
        
        // Skip the '+' prefix
        if (!TryAdvance(1))
            return false;
        
        if (!TryReadLine(out var lineSpan, out var lineLength))
            return false;
        
        // Convert ReadOnlySequence to ReadOnlyMemory for RespireValue
        var buffer = GetBufferFromSequence(lineSpan, lineLength);
        value = RespireValue.SimpleString(buffer, 0, lineLength);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadError(out RespireValue value)
    {
        value = default;
        
        // Skip the '-' prefix
        if (!TryAdvance(1))
            return false;
        
        if (!TryReadLine(out var lineSpan, out var lineLength))
            return false;
        
        var buffer = GetBufferFromSequence(lineSpan, lineLength);
        value = RespireValue.Error(buffer, 0, lineLength);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadInteger(out RespireValue value)
    {
        value = default;
        
        // Skip the ':' prefix
        if (!TryAdvance(1))
            return false;
        
        if (!TryReadLine(out var lineSpan, out var lineLength))
            return false;
        
        if (!TryParseInteger(lineSpan, lineLength, out var intValue))
            return false;
        
        value = RespireValue.Integer(intValue);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBulkString(out RespireValue value)
    {
        value = default;
        
        // Skip the '$' prefix
        if (!TryAdvance(1))
            return false;
        
        // Read the length line
        if (!TryReadLine(out var lengthSpan, out var lengthLineLength))
            return false;
        
        if (!TryParseInteger(lengthSpan, lengthLineLength, out var length))
            return false;
        
        if (length == -1)
        {
            value = RespireValue.Null;
            return true;
        }
        
        if (length == 0)
        {
            value = RespireValue.BulkString(ReadOnlyMemory<byte>.Empty, 0, 0);
            return true;
        }
        
        // Read the data bytes
        if (!TryReadBytes((int)length, out var dataSpan))
            return false;
        
        // Skip the trailing \r\n
        if (!TryAdvance(2))
            return false;
        
        var buffer = GetBufferFromSequence(dataSpan, (int)length);
        value = RespireValue.BulkString(buffer, 0, (int)length);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBoolean(out RespireValue value)
    {
        value = default;
        
        // Skip the '#' prefix
        if (!TryAdvance(1))
            return false;
        
        if (!TryPeekByte(out var boolByte))
            return false;
        
        if (!TryAdvance(1))
            return false;
        
        // Skip the trailing \r\n
        if (!TryReadLine(out _, out _))
            return false;
        
        value = boolByte == (byte)'t' ? RespireValue.True : RespireValue.False;
        return true;
    }
    
    private bool TryReadArray(out RespireValue value)
    {
        // Arrays require allocation for the results, so we'll skip this for zero-alloc version
        // This would need to return a different structure that can hold multiple values
        value = default;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryPeekByte(out byte value)
    {
        value = 0;
        
        if (IsAtEnd)
            return false;
        
        var span = _sequence.Slice(_position, 1);
        if (span.IsEmpty)
            return false;
        
        value = span.FirstSpan[0];
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAdvance(int count)
    {
        var remaining = _sequence.Slice(_position);
        if (remaining.Length < count)
            return false;
        
        _position = _sequence.GetPosition(count, _position);
        _examined = _position;
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadLine(out ReadOnlySequence<byte> line, out int length)
    {
        line = default;
        length = 0;
        
        var remaining = _sequence.Slice(_position);
        var reader = new SequenceReader<byte>(remaining);
        
        if (!reader.TryReadTo(out ReadOnlySequence<byte> lineData, new byte[] { (byte)'\r', (byte)'\n' }))
            return false;
        
        line = lineData;
        length = (int)lineData.Length;
        
        // Advance past the line and CRLF
        _position = reader.Position;
        _consumed = _position;
        _examined = _position;
        
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBytes(int count, out ReadOnlySequence<byte> data)
    {
        data = default;
        
        var remaining = _sequence.Slice(_position);
        if (remaining.Length < count)
            return false;
        
        data = remaining.Slice(0, count);
        _position = _sequence.GetPosition(count, _position);
        _examined = _position;
        
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryParseInteger(ReadOnlySequence<byte> sequence, int length, out long value)
    {
        value = 0;
        
        if (length == 0)
            return false;
        
        // Handle single segment optimization
        if (sequence.IsSingleSegment)
        {
            return TryParseIntegerFromSpan(sequence.FirstSpan, out value);
        }
        
        // Multi-segment parsing
        var negative = false;
        var reader = new SequenceReader<byte>(sequence);
        
        if (!reader.TryRead(out var first))
            return false;
        
        if (first == (byte)'-')
        {
            negative = true;
            if (!reader.TryRead(out first))
                return false;
        }
        
        if (first < (byte)'0' || first > (byte)'9')
            return false;
        
        value = first - (byte)'0';
        
        while (reader.TryRead(out var digit))
        {
            if (digit < (byte)'0' || digit > (byte)'9')
                return false;
            
            value = value * 10 + (digit - (byte)'0');
        }
        
        if (negative)
            value = -value;
        
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseIntegerFromSpan(ReadOnlySpan<byte> span, out long value)
    {
        value = 0;
        
        if (span.Length == 0)
            return false;
        
        var negative = false;
        var index = 0;
        
        if (span[0] == (byte)'-')
        {
            negative = true;
            index = 1;
        }
        
        if (index >= span.Length)
            return false;
        
        for (int i = index; i < span.Length; i++)
        {
            var digit = span[i];
            if (digit < (byte)'0' || digit > (byte)'9')
                return false;
            
            value = value * 10 + (digit - (byte)'0');
        }
        
        if (negative)
            value = -value;
        
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlyMemory<byte> GetBufferFromSequence(ReadOnlySequence<byte> sequence, int length)
    {
        // For zero-allocation parsing, we need to get a ReadOnlyMemory from the sequence
        // If it's a single segment, we can create a ReadOnlyMemory directly
        if (sequence.IsSingleSegment && sequence.First.Length >= length)
        {
            return sequence.First.Slice(0, length);
        }
        
        // For multi-segment sequences, we'd need to copy data
        // In a real implementation, you might want to keep a reference to the original buffer
        // For now, we'll create a copy (this reduces the zero-allocation benefit)
        var array = sequence.Slice(0, length).ToArray();
        return new ReadOnlyMemory<byte>(array);
    }
    
    /// <summary>
    /// Marks data as consumed up to the current position
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkConsumed()
    {
        _consumed = _position;
    }
    
    /// <summary>
    /// Marks data as examined up to the current position
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkExamined()
    {
        _examined = _position;
    }
}