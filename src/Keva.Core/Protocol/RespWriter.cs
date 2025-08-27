using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;

namespace Keva.Core.Protocol;

public ref struct RespWriter
{
    private readonly IBufferWriter<byte> _writer;
    private readonly ArrayPool<byte> _pool;
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();
    private static readonly byte[] NullBulkString = "$-1\r\n"u8.ToArray();
    private static readonly byte[] NullArray = "*-1\r\n"u8.ToArray();
    private static readonly byte[] EmptyArray = "*0\r\n"u8.ToArray();
    
    public RespWriter(IBufferWriter<byte> writer, ArrayPool<byte>? pool = null)
    {
        _writer = writer;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }
    
    public RespWriter(PipeWriter writer, ArrayPool<byte>? pool = null)
        : this((IBufferWriter<byte>)writer, pool)
    {
    }
    
    public void Write(RespValue value)
    {
        switch (value.Type)
        {
            case RespDataType.SimpleString:
                WriteSimpleString(value.AsBytes());
                break;
            case RespDataType.Error:
                WriteError(value.AsBytes());
                break;
            case RespDataType.Integer:
                WriteInteger(value.AsInteger());
                break;
            case RespDataType.BulkString:
                WriteBulkString(value.IsNull ? null : value.AsBytes());
                break;
            case RespDataType.Array:
                WriteArray(value.AsArray());
                break;
            case RespDataType.Null:
                WriteNull();
                break;
            case RespDataType.Boolean:
                WriteBoolean(value.AsBoolean());
                break;
            case RespDataType.Double:
                WriteDouble(value.AsDouble());
                break;
            case RespDataType.Map:
                WriteMap(value.AsArray());
                break;
            case RespDataType.Set:
                WriteSet(value.AsArray());
                break;
            default:
                throw new NotSupportedException($"Type {value.Type} is not supported");
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCommand(ReadOnlySpan<byte> command, params ReadOnlyMemory<byte>[] args)
    {
        var span = _writer.GetSpan(16);
        span[0] = (byte)'*';
        
        var count = 1 + args.Length;
        
        if (!Utf8Formatter.TryFormat(count, span.Slice(1), out int written))
        {
            throw new InvalidOperationException("Failed to format array count");
        }

        _writer.Advance(1 + written);
        
        WriteCrlf();
        
        WriteBulkString(command);
        
        foreach (var arg in args)
        {
            WriteBulkString(arg.Span);
        }
    }
    
    public void WriteCommand(string command, params string[] args)
    {
        var commandBytes = Encoding.UTF8.GetBytes(command);
        WriteCommand(commandBytes.AsSpan(), ConvertArgs(args));
    }
    
    private ReadOnlyMemory<byte>[] ConvertArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return Array.Empty<ReadOnlyMemory<byte>>();
        }

        var result = new ReadOnlyMemory<byte>[args.Length];
        
        for (int i = 0; i < args.Length; i++)
        {
            result[i] = Encoding.UTF8.GetBytes(args[i]);
        }
        
        return result;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSimpleString(ReadOnlyMemory<byte> value)
    {
        var span = _writer.GetSpan(1);
        span[0] = (byte)RespDataType.SimpleString;
        _writer.Advance(1);
        
        _writer.Write(value.Span);
        WriteCrlf();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteError(ReadOnlyMemory<byte> message)
    {
        var span = _writer.GetSpan(1);
        span[0] = (byte)RespDataType.Error;
        _writer.Advance(1);
        
        _writer.Write(message.Span);
        WriteCrlf();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInteger(long value)
    {
        var span = _writer.GetSpan(32); // Max long + : + \r\n
        span[0] = (byte)RespDataType.Integer;
        
        if (!Utf8Formatter.TryFormat(value, span.Slice(1), out int written))
        {
            throw new InvalidOperationException("Failed to format integer");
        }

        _writer.Advance(1 + written);
        WriteCrlf();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteBulkString(ReadOnlyMemory<byte>? value)
    {
        if (!value.HasValue)
        {
            _writer.Write(NullBulkString);
            return;
        }
        
        var data = value.Value;
        var span = _writer.GetSpan(32);
        span[0] = (byte)RespDataType.BulkString;
        
        if (!Utf8Formatter.TryFormat(data.Length, span.Slice(1), out int written))
        {
            throw new InvalidOperationException("Failed to format bulk string length");
        }

        _writer.Advance(1 + written);
        WriteCrlf();
        
        if (data.Length > 0)
        {
            _writer.Write(data.Span);
        }
        WriteCrlf();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteBulkString(ReadOnlySpan<byte> value)
    {
        var span = _writer.GetSpan(32);
        span[0] = (byte)RespDataType.BulkString;
        
        if (!Utf8Formatter.TryFormat(value.Length, span.Slice(1), out int written))
        {
            throw new InvalidOperationException("Failed to format bulk string length");
        }

        _writer.Advance(1 + written);
        WriteCrlf();
        
        if (value.Length > 0)
        {
            _writer.Write(value);
        }
        WriteCrlf();
    }
    
    private void WriteArray(ReadOnlyMemory<RespValue> values)
    {
        var span = _writer.GetSpan(32);
        span[0] = (byte)RespDataType.Array;
        
        if (!Utf8Formatter.TryFormat(values.Length, span.Slice(1), out int written))
        {
            throw new InvalidOperationException("Failed to format array count");
        }

        _writer.Advance(1 + written);
        WriteCrlf();
        
        foreach (var value in values.Span)
        {
            Write(value);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteNull()
    {
        var span = _writer.GetSpan(3);
        span[0] = (byte)RespDataType.Null;
        span[1] = (byte)'\r';
        span[2] = (byte)'\n';
        _writer.Advance(3);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteBoolean(bool value)
    {
        var span = _writer.GetSpan(4);
        span[0] = (byte)RespDataType.Boolean;
        span[1] = value ? (byte)'t' : (byte)'f';
        span[2] = (byte)'\r';
        span[3] = (byte)'\n';
        _writer.Advance(4);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteDouble(double value)
    {
        var span = _writer.GetSpan(64);
        span[0] = (byte)RespDataType.Double;
        
        if (!Utf8Formatter.TryFormat(value, span.Slice(1), out int written))
        {
            throw new InvalidOperationException("Failed to format double");
        }

        _writer.Advance(1 + written);
        WriteCrlf();
    }
    
    private void WriteMap(ReadOnlyMemory<RespValue> values)
    {
        var span = _writer.GetSpan(32);
        span[0] = (byte)RespDataType.Map;
        
        var count = values.Length / 2; // Map stores key-value pairs
        if (!Utf8Formatter.TryFormat(count, span.Slice(1), out int written))
        {
            throw new InvalidOperationException("Failed to format map count");
        }

        _writer.Advance(1 + written);
        WriteCrlf();
        
        foreach (var value in values.Span)
        {
            Write(value);
        }
    }
    
    private void WriteSet(ReadOnlyMemory<RespValue> values)
    {
        var span = _writer.GetSpan(32);
        span[0] = (byte)RespDataType.Set;
        
        if (!Utf8Formatter.TryFormat(values.Length, span.Slice(1), out int written))
        {
            throw new InvalidOperationException("Failed to format set count");
        }

        _writer.Advance(1 + written);
        WriteCrlf();
        
        foreach (var value in values.Span)
        {
            Write(value);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteCrlf()
    {
        _writer.Write(Crlf);
    }
}