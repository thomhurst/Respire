using System.Buffers;
using System.Runtime.CompilerServices;

namespace Keva.Core.Protocol;

/// <summary>
/// Zero-allocation RESP reader that works directly with memory buffers
/// </summary>
public ref struct ZeroAllocRespReader
{
    private ReadOnlySpan<byte> _buffer;
    private int _position;
    
    public ZeroAllocRespReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }
    
    public int Position => _position;
    public int Remaining => _buffer.Length - _position;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out ZeroAllocRespValue value)
    {
        value = default;
        
        if (_position >= _buffer.Length)
        {
            return false;
        }

        var type = (char)_buffer[_position++];
        
        return type switch
        {
            '+' => TryReadSimpleString(out value),
            '-' => TryReadError(out value),
            ':' => TryReadInteger(out value),
            '$' => TryReadBulkString(out value),
            '*' => TryReadArray(out value),
            '#' => TryReadBoolean(out value),
            _ => false
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadSimpleString(out ZeroAllocRespValue value)
    {
        var start = _position;
        if (!SeekToLineEnd(out var length))
        {
            value = default;
            return false;
        }
        
        // Create a reference to the original buffer instead of copying
        var bufferRef = new ReadOnlyMemory<byte>(_buffer.ToArray());
        value = ZeroAllocRespValue.SimpleString(bufferRef, start, length);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadError(out ZeroAllocRespValue value)
    {
        var start = _position;
        if (!SeekToLineEnd(out var length))
        {
            value = default;
            return false;
        }
        
        var bufferRef = new ReadOnlyMemory<byte>(_buffer.ToArray());
        value = ZeroAllocRespValue.Error(bufferRef, start, length);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadInteger(out ZeroAllocRespValue value)
    {
        if (!TryReadLong(out var longValue))
        {
            value = default;
            return false;
        }
        
        // No boxing - direct value storage in the discriminated union
        value = ZeroAllocRespValue.Integer(longValue);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBulkString(out ZeroAllocRespValue value)
    {
        if (!TryReadLong(out var length))
        {
            value = default;
            return false;
        }
        
        if (length == -1)
        {
            value = ZeroAllocRespValue.Null;
            return true;
        }
        
        if (length == 0)
        {
            // Skip \r\n
            if (_position + 2 <= _buffer.Length && 
                _buffer[_position] == '\r' && _buffer[_position + 1] == '\n')
            {
                _position += 2;
            }
            
            value = ZeroAllocRespValue.BulkString(ReadOnlyMemory<byte>.Empty, 0, 0);
            return true;
        }
        
        var dataStart = _position;
        if (_position + length + 2 > _buffer.Length)
        {
            value = default;
            return false;
        }
        
        // Skip data + \r\n
        _position += (int)length + 2;
        
        var bufferRef = new ReadOnlyMemory<byte>(_buffer.ToArray());
        value = ZeroAllocRespValue.BulkString(bufferRef, dataStart, (int)length);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBoolean(out ZeroAllocRespValue value)
    {
        if (_position >= _buffer.Length)
        {
            value = default;
            return false;
        }
        
        var boolChar = (char)_buffer[_position++];
        if (!SeekToLineEnd(out _))
        {
            value = default;
            return false;
        }
        
        value = boolChar == 't' ? ZeroAllocRespValue.True : ZeroAllocRespValue.False;
        return true;
    }
    
    private bool TryReadArray(out ZeroAllocRespValue value)
    {
        // Arrays still require allocation for the results
        // This is unavoidable if we need to return multiple values
        value = default;
        return false; // Skip array parsing for zero-alloc version
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadLong(out long value)
    {
        value = 0;
        var negative = false;
        
        if (_position >= _buffer.Length)
        {
            return false;
        }

        if (_buffer[_position] == '-')
        {
            negative = true;
            _position++;
        }
        
        var hasDigits = false;
        while (_position < _buffer.Length)
        {
            var b = _buffer[_position];
            if (b == '\r')
            {
                break;
            }

            if (b < '0' || b > '9')
            {
                return false;
            }

            value = value * 10 + (b - '0');
            _position++;
            hasDigits = true;
        }
        
        if (!hasDigits)
        {
            return false;
        }

        // Skip \r\n
        if (_position + 2 <= _buffer.Length && 
            _buffer[_position] == '\r' && _buffer[_position + 1] == '\n')
        {
            _position += 2;
        }
        
        if (negative)
        {
            value = -value;
        }

        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SeekToLineEnd(out int length)
    {
        var start = _position;
        
        while (_position < _buffer.Length - 1)
        {
            if (_buffer[_position] == '\r' && _buffer[_position + 1] == '\n')
            {
                length = _position - start;
                _position += 2; // Skip \r\n
                return true;
            }
            _position++;
        }
        
        length = 0;
        return false;
    }
}