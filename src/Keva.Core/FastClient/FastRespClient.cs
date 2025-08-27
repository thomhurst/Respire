using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Keva.Core.Protocol;

namespace Keva.Core.FastClient;

/// <summary>
/// Ultra-fast, minimal allocation RESP client optimized for performance
/// </summary>
public sealed class FastRespClient : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _sendBuffer;
    private readonly byte[] _receiveBuffer;
    private readonly object _syncLock = new();
    private int _receiveOffset;
    private int _receiveCount;
    
    public FastRespClient(string host, int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            SendBufferSize = 65536,
            ReceiveBufferSize = 65536
        };
        
        _sendBuffer = new byte[65536];
        _receiveBuffer = new byte[65536];
        
        _socket.Connect(host, port);
    }
    
    public string? Ping()
    {
        lock (_syncLock)
        {
            WritePing();
            return ReadSimpleString();
        }
    }
    
    public bool Set(string key, string value)
    {
        lock (_syncLock)
        {
            WriteSet(key, value);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public string? Get(string key)
    {
        lock (_syncLock)
        {
            WriteGet(key);
            return ReadBulkString();
        }
    }
    
    public bool Exists(string key)
    {
        lock (_syncLock)
        {
            WriteExists(key);
            return ReadInteger() > 0;
        }
    }
    
    public bool Del(string key)
    {
        lock (_syncLock)
        {
            WriteDel(key);
            return ReadInteger() > 0;
        }
    }
    
    public long Incr(string key)
    {
        lock (_syncLock)
        {
            WriteIncr(key);
            return ReadInteger();
        }
    }
    
    public string?[] MGet(params string[] keys)
    {
        lock (_syncLock)
        {
            WriteMGet(keys);
            return ReadArray();
        }
    }
    
    public void Pipeline(Action<FastRespPipeline> configure)
    {
        lock (_syncLock)
        {
            var pipeline = new FastRespPipeline(this);
            configure(pipeline);
            pipeline.Execute();
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePing()
    {
        var span = _sendBuffer.AsSpan();
        var written = 0;
        
        // *1\r\n$4\r\nPING\r\n
        span[written++] = (byte)'*';
        span[written++] = (byte)'1';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'$';
        span[written++] = (byte)'4';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'P';
        span[written++] = (byte)'I';
        span[written++] = (byte)'N';
        span[written++] = (byte)'G';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSet(string key, string value)
    {
        var span = _sendBuffer.AsSpan();
        var written = 0;
        
        // *3\r\n$3\r\nSET\r\n$<key_len>\r\n<key>\r\n$<val_len>\r\n<val>\r\n
        span[written++] = (byte)'*';
        span[written++] = (byte)'3';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'$';
        span[written++] = (byte)'3';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'S';
        span[written++] = (byte)'E';
        span[written++] = (byte)'T';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        // Write key
        written += WriteBulkString(span[written..], key);
        
        // Write value
        written += WriteBulkString(span[written..], value);
        
        _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteGet(string key)
    {
        var span = _sendBuffer.AsSpan();
        var written = 0;
        
        // *2\r\n$3\r\nGET\r\n$<key_len>\r\n<key>\r\n
        span[written++] = (byte)'*';
        span[written++] = (byte)'2';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'$';
        span[written++] = (byte)'3';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'G';
        span[written++] = (byte)'E';
        span[written++] = (byte)'T';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        // Write key
        written += WriteBulkString(span[written..], key);
        
        _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteExists(string key)
    {
        var span = _sendBuffer.AsSpan();
        var written = 0;
        
        // *2\r\n$6\r\nEXISTS\r\n$<key_len>\r\n<key>\r\n
        span[written++] = (byte)'*';
        span[written++] = (byte)'2';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'$';
        span[written++] = (byte)'6';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'E';
        span[written++] = (byte)'X';
        span[written++] = (byte)'I';
        span[written++] = (byte)'S';
        span[written++] = (byte)'T';
        span[written++] = (byte)'S';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        // Write key
        written += WriteBulkString(span[written..], key);
        
        _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteDel(string key)
    {
        var span = _sendBuffer.AsSpan();
        var written = 0;
        
        // *2\r\n$3\r\nDEL\r\n$<key_len>\r\n<key>\r\n
        span[written++] = (byte)'*';
        span[written++] = (byte)'2';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'$';
        span[written++] = (byte)'3';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'D';
        span[written++] = (byte)'E';
        span[written++] = (byte)'L';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        // Write key
        written += WriteBulkString(span[written..], key);
        
        _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteIncr(string key)
    {
        var span = _sendBuffer.AsSpan();
        var written = 0;
        
        // *2\r\n$4\r\nINCR\r\n$<key_len>\r\n<key>\r\n
        span[written++] = (byte)'*';
        span[written++] = (byte)'2';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'$';
        span[written++] = (byte)'4';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'I';
        span[written++] = (byte)'N';
        span[written++] = (byte)'C';
        span[written++] = (byte)'R';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        // Write key
        written += WriteBulkString(span[written..], key);
        
        _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMGet(string[] keys)
    {
        var span = _sendBuffer.AsSpan();
        var written = 0;
        
        // *<1+keys>\r\n$4\r\nMGET\r\n
        span[written++] = (byte)'*';
        written += WriteIntegerAsBytes(span[written..], keys.Length + 1);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'$';
        span[written++] = (byte)'4';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        span[written++] = (byte)'M';
        span[written++] = (byte)'G';
        span[written++] = (byte)'E';
        span[written++] = (byte)'T';
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        // Write keys
        foreach (var key in keys)
        {
            written += WriteBulkString(span[written..], key);
        }
        
        _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
    }
    
    internal int WriteCommand(Span<byte> span, string command, string[] args)
    {
        var written = 0;
        
        // *<1+args>\r\n
        span[written++] = (byte)'*';
        written += WriteIntegerAsBytes(span[written..], args.Length + 1);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        // Command
        written += WriteBulkString(span[written..], command);
        
        // Arguments
        foreach (var arg in args)
        {
            written += WriteBulkString(span[written..], arg);
        }
        
        return written;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int WriteBulkString(Span<byte> span, string value)
    {
        var written = 0;
        var bytes = Encoding.UTF8.GetByteCount(value);
        
        // $<len>\r\n
        span[written++] = (byte)'$';
        written += WriteIntegerAsBytes(span[written..], bytes);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        // <data>\r\n
        written += Encoding.UTF8.GetBytes(value, span[written..]);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        return written;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe int WriteIntegerAsBytes(Span<byte> span, int value)
    {
        if (value == 0)
        {
            span[0] = (byte)'0';
            return 1;
        }
        
        var written = 0;
        var temp = stackalloc byte[11]; // max int is 10 digits + sign
        var tempLen = 0;
        
        if (value < 0)
        {
            span[written++] = (byte)'-';
            value = -value;
        }
        
        while (value > 0)
        {
            temp[tempLen++] = (byte)('0' + (value % 10));
            value /= 10;
        }
        
        // Write in reverse
        while (tempLen > 0)
        {
            span[written++] = temp[--tempLen];
        }
        
        return written;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureData()
    {
        if (_receiveOffset >= _receiveCount)
        {
            _receiveOffset = 0;
            _receiveCount = _socket.Receive(_receiveBuffer);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string? ReadSimpleString()
    {
        EnsureData();
        
        if (_receiveBuffer[_receiveOffset++] != '+')
        {
            throw new InvalidOperationException("Expected simple string");
        }

        var start = _receiveOffset;
        while (true)
        {
            EnsureData();
            if (_receiveBuffer[_receiveOffset] == '\r')
            {
                var result = Encoding.UTF8.GetString(_receiveBuffer, start, _receiveOffset - start);
                _receiveOffset += 2; // Skip \r\n
                return result;
            }
            _receiveOffset++;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string? ReadBulkString()
    {
        EnsureData();
        
        if (_receiveBuffer[_receiveOffset++] != '$')
        {
            throw new InvalidOperationException("Expected bulk string");
        }

        var length = ReadIntegerUntilCrlf();
        if (length == -1)
        {
            return null;
        }

        var bytes = new byte[(int)length];
        var bytesRead = 0;
        
        while (bytesRead < length)
        {
            EnsureData();
            var toRead = (int)Math.Min(length - bytesRead, _receiveCount - _receiveOffset);
            Buffer.BlockCopy(_receiveBuffer, _receiveOffset, bytes, bytesRead, toRead);
            _receiveOffset += toRead;
            bytesRead += toRead;
        }
        
        EnsureData();
        _receiveOffset += 2; // Skip \r\n
        
        return Encoding.UTF8.GetString(bytes);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadInteger()
    {
        EnsureData();
        
        if (_receiveBuffer[_receiveOffset++] != ':')
        {
            throw new InvalidOperationException("Expected integer");
        }

        return ReadIntegerUntilCrlf();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadIntegerUntilCrlf()
    {
        var negative = false;
        long value = 0;
        
        EnsureData();
        if (_receiveBuffer[_receiveOffset] == '-')
        {
            negative = true;
            _receiveOffset++;
        }
        
        while (true)
        {
            EnsureData();
            var b = _receiveBuffer[_receiveOffset];
            if (b == '\r')
            {
                _receiveOffset += 2; // Skip \r\n
                return negative ? -value : value;
            }
            value = value * 10 + (b - '0');
            _receiveOffset++;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string?[] ReadArray()
    {
        EnsureData();
        
        if (_receiveBuffer[_receiveOffset++] != '*')
        {
            throw new InvalidOperationException("Expected array");
        }

        var count = (int)ReadIntegerUntilCrlf();
        if (count == -1)
        {
            return Array.Empty<string>();
        }

        var result = new string?[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = ReadBulkString();
        }
        
        return result;
    }
    
    internal void SendBuffer(byte[] buffer, int count)
    {
        _socket.Send(buffer, 0, count, SocketFlags.None);
    }
    
    internal string? ReadResponse()
    {
        EnsureData();
        var type = (char)_receiveBuffer[_receiveOffset];
        
        return type switch
        {
            '+' => ReadSimpleString(),
            '$' => ReadBulkString(),
            ':' => ReadInteger().ToString(),
            _ => throw new InvalidOperationException($"Unexpected response type: {type}")
        };
    }
    
    public void Dispose()
    {
        _socket?.Dispose();
    }
}