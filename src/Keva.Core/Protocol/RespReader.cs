using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace Keva.Core.Protocol;

public ref struct RespReader
{
    private ReadOnlySequence<byte> _buffer;
    private SequenceReader<byte> _reader;
    private readonly ArrayPool<byte> _arrayPool;
    private readonly ArrayPool<RespValue> _valuePool;
    
    public RespReader(ReadOnlySequence<byte> buffer, ArrayPool<byte>? arrayPool = null, ArrayPool<RespValue>? valuePool = null)
    {
        _buffer = buffer;
        _reader = new SequenceReader<byte>(buffer);
        _arrayPool = arrayPool ?? ArrayPool<byte>.Shared;
        _valuePool = valuePool ?? ArrayPool<RespValue>.Shared;
    }
    
    public RespReader(ReadOnlyMemory<byte> buffer, ArrayPool<byte>? arrayPool = null, ArrayPool<RespValue>? valuePool = null)
        : this(new ReadOnlySequence<byte>(buffer), arrayPool, valuePool)
    {
    }
    
    public bool TryRead(out RespValue value)
    {
        value = default;
        
        if (!_reader.TryRead(out byte typeMarker))
        {
            return false;
        }

        var type = (RespDataType)typeMarker;
        
        return type switch
        {
            RespDataType.SimpleString => TryReadSimpleString(out value),
            RespDataType.Error => TryReadError(out value),
            RespDataType.Integer => TryReadInteger(out value),
            RespDataType.BulkString => TryReadBulkString(out value),
            RespDataType.Array => TryReadArray(out value),
            RespDataType.Null => TryReadNull(out value),
            RespDataType.Boolean => TryReadBoolean(out value),
            RespDataType.Double => TryReadDouble(out value),
            RespDataType.Map => TryReadMap(out value),
            RespDataType.Set => TryReadSet(out value),
            _ => false
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadSimpleString(out RespValue value)
    {
        if (TryReadLine(out var line))
        {
            value = RespValue.SimpleString(line);
            return true;
        }
        value = default;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadError(out RespValue value)
    {
        if (TryReadLine(out var line))
        {
            value = RespValue.Error(line);
            return true;
        }
        value = default;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadInteger(out RespValue value)
    {
        if (TryReadLine(out var line))
        {
            if (Utf8Parser.TryParse(line.Span, out long number, out _))
            {
                value = RespValue.Integer(number);
                return true;
            }
        }
        value = default;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBulkString(out RespValue value)
    {
        if (!TryReadLine(out var lengthLine))
        {
            value = default;
            return false;
        }
        
        if (!Utf8Parser.TryParse(lengthLine.Span, out int length, out _))
        {
            value = default;
            return false;
        }
        
        if (length == -1)
        {
            value = RespValue.Null;
            return true;
        }
        
        if (length == 0)
        {
            value = RespValue.EmptyBulkString;
            if (!SkipCrlf())
            {
                return false;
            }
            return true;
        }
        
        if (_reader.Remaining < length + 2) // +2 for CRLF
        {
            value = default;
            return false;
        }
        
        var data = _buffer.Slice(_reader.Position, length);
        _reader.Advance(length);
        
        if (!SkipCrlf())
        {
            value = default;
            return false;
        }
        
        // Create value without allocation by using the original buffer slice
        value = RespValue.BulkString(data.First);
        return true;
    }
    
    private bool TryReadArray(out RespValue value)
    {
        if (!TryReadLine(out var countLine))
        {
            value = default;
            return false;
        }
        
        if (!Utf8Parser.TryParse(countLine.Span, out int count, out _))
        {
            value = default;
            return false;
        }
        
        if (count == -1)
        {
            value = RespValue.Null;
            return true;
        }
        
        if (count == 0)
        {
            value = RespValue.EmptyArray;
            return true;
        }
        
        var items = _valuePool.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (!TryRead(out items[i]))
                {
                    value = default;
                    return false;
                }
            }
            
            // Copy to exact-sized array to avoid keeping the pooled array
            var result = new RespValue[count];
            Array.Copy(items, result, count);
            value = RespValue.Array(result);
            return true;
        }
        finally
        {
            _valuePool.Return(items, clearArray: true);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadNull(out RespValue value)
    {
        if (SkipCrlf())
        {
            value = RespValue.Null;
            return true;
        }
        value = default;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBoolean(out RespValue value)
    {
        if (_reader.TryRead(out byte b))
        {
            if (SkipCrlf())
            {
                value = b == (byte)'t' ? RespValue.True : RespValue.False;
                return true;
            }
        }
        value = default;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadDouble(out RespValue value)
    {
        if (TryReadLine(out var line))
        {
            if (Utf8Parser.TryParse(line.Span, out double number, out _))
            {
                value = RespValue.Double(number);
                return true;
            }
        }
        value = default;
        return false;
    }
    
    private bool TryReadMap(out RespValue value)
    {
        if (!TryReadLine(out var countLine))
        {
            value = default;
            return false;
        }
        
        if (!Utf8Parser.TryParse(countLine.Span, out int count, out _))
        {
            value = default;
            return false;
        }
        
        if (count == -1)
        {
            value = RespValue.Null;
            return true;
        }
        
        var items = _valuePool.Rent(count * 2); // Maps have key-value pairs
        try
        {
            for (int i = 0; i < count * 2; i++)
            {
                if (!TryRead(out items[i]))
                {
                    value = default;
                    return false;
                }
            }
            
            var result = new RespValue[count * 2];
            Array.Copy(items, result, count * 2);
            value = RespValue.Map(result);
            return true;
        }
        finally
        {
            _valuePool.Return(items, clearArray: true);
        }
    }
    
    private bool TryReadSet(out RespValue value)
    {
        if (!TryReadLine(out var countLine))
        {
            value = default;
            return false;
        }
        
        if (!Utf8Parser.TryParse(countLine.Span, out int count, out _))
        {
            value = default;
            return false;
        }
        
        if (count == -1)
        {
            value = RespValue.Null;
            return true;
        }
        
        var items = _valuePool.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (!TryRead(out items[i]))
                {
                    value = default;
                    return false;
                }
            }
            
            var result = new RespValue[count];
            Array.Copy(items, result, count);
            value = RespValue.Set(result);
            return true;
        }
        finally
        {
            _valuePool.Return(items, clearArray: true);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadLine(out ReadOnlyMemory<byte> line)
    {
        if (_reader.TryReadTo(out ReadOnlySequence<byte> sequence, (byte)'\n'))
        {
            // Remove trailing \r if present
            var length = sequence.Length;
            if (length > 0 && sequence.IsSingleSegment)
            {
                var span = sequence.FirstSpan;
                if (span[span.Length - 1] == '\r')
                {
                    line = sequence.First.Slice(0, (int)(length - 1));
                    return true;
                }
            }
            line = sequence.First;
            return true;
        }
        
        line = default;
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SkipCrlf()
    {
        if (_reader.Remaining >= 2)
        {
            if (_reader.TryPeek(out byte b1) && b1 == '\r')
            {
                _reader.Advance(1);
                if (_reader.TryPeek(out byte b2) && b2 == '\n')
                {
                    _reader.Advance(1);
                    return true;
                }
            }
        }
        return false;
    }
    
    public void Reset(ReadOnlySequence<byte> buffer)
    {
        _buffer = buffer;
        _reader = new SequenceReader<byte>(buffer);
    }
    
    public long Consumed => _reader.Consumed;
    public long Remaining => _reader.Remaining;
}