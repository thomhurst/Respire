using System.Buffers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Keva.Core.FastClient;

/// <summary>
/// Ultra-optimized RESP client with zero allocations for common operations
/// </summary>
public sealed class UltraFastRespClient : IDisposable
{
    // Pre-compiled RESP commands to avoid allocations
    private static readonly byte[] PingCommand = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    private static readonly byte[] GetPrefix = "*2\r\n$3\r\nGET\r\n"u8.ToArray();
    private static readonly byte[] SetPrefix = "*3\r\n$3\r\nSET\r\n"u8.ToArray();
    private static readonly byte[] DelPrefix = "*2\r\n$3\r\nDEL\r\n"u8.ToArray();
    private static readonly byte[] ExistsPrefix = "*2\r\n$6\r\nEXISTS\r\n"u8.ToArray();
    private static readonly byte[] IncrPrefix = "*2\r\n$4\r\nINCR\r\n"u8.ToArray();
    
    // Interned common responses
    private static readonly string PongResponse = "PONG";
    private static readonly string OkResponse = "OK";
    
    // Pre-allocated digit strings for small numbers
    private static readonly string[] SmallNumbers = new string[100];
    
    static UltraFastRespClient()
    {
        for (int i = 0; i < SmallNumbers.Length; i++)
        {
            SmallNumbers[i] = i.ToString();
        }
    }
    
    private readonly Socket _socket;
    private readonly byte[] _sendBuffer;
    private readonly byte[] _receiveBuffer;
    private readonly ArrayPool<byte> _arrayPool;
    private readonly object _syncLock = new();
    private int _receiveOffset;
    private int _receiveCount;
    
    // Reusable StringBuilder for string operations
    private readonly StringBuilder _stringBuilder = new(256);
    
    public UltraFastRespClient(string host, int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            SendBufferSize = 131072, // 128KB
            ReceiveBufferSize = 131072
        };
        
        _sendBuffer = GC.AllocateUninitializedArray<byte>(131072, pinned: true);
        _receiveBuffer = GC.AllocateUninitializedArray<byte>(131072, pinned: true);
        _arrayPool = ArrayPool<byte>.Shared;
        
        _socket.Connect(host, port);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Ping()
    {
        lock (_syncLock)
        {
            // Send pre-compiled PING command
            _socket.Send(PingCommand);
            
            // Read response
            EnsureData();
            if (_receiveBuffer[_receiveOffset] == '+')
            {
                _receiveOffset++;
                
                // Check for PONG response without allocation
                if (_receiveCount - _receiveOffset >= 6 &&
                    _receiveBuffer[_receiveOffset] == 'P' &&
                    _receiveBuffer[_receiveOffset + 1] == 'O' &&
                    _receiveBuffer[_receiveOffset + 2] == 'N' &&
                    _receiveBuffer[_receiveOffset + 3] == 'G' &&
                    _receiveBuffer[_receiveOffset + 4] == '\r' &&
                    _receiveBuffer[_receiveOffset + 5] == '\n')
                {
                    _receiveOffset += 6;
                    return true;
                }
            }
            
            SkipLine();
            return false;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Set(string key, string value)
    {
        lock (_syncLock)
        {
            var written = SetPrefix.Length;
            SetPrefix.CopyTo(_sendBuffer, 0);
            
            // Write key
            written += WriteBulkString(_sendBuffer.AsSpan()[written..], key);
            
            // Write value  
            written += WriteBulkString(_sendBuffer.AsSpan()[written..], value);
            
            _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
            
            // Read response
            EnsureData();
            if (_receiveBuffer[_receiveOffset] == '+')
            {
                _receiveOffset++;
                
                // Check for OK response without allocation
                if (_receiveCount - _receiveOffset >= 4 &&
                    _receiveBuffer[_receiveOffset] == 'O' &&
                    _receiveBuffer[_receiveOffset + 1] == 'K' &&
                    _receiveBuffer[_receiveOffset + 2] == '\r' &&
                    _receiveBuffer[_receiveOffset + 3] == '\n')
                {
                    _receiveOffset += 4;
                    return true;
                }
            }
            
            SkipLine();
            return false;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? Get(string key)
    {
        lock (_syncLock)
        {
            var written = GetPrefix.Length;
            GetPrefix.CopyTo(_sendBuffer, 0);
            
            // Write key
            written += WriteBulkString(_sendBuffer.AsSpan()[written..], key);
            
            _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
            
            return ReadBulkStringOptimized();
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Exists(string key)
    {
        lock (_syncLock)
        {
            var written = ExistsPrefix.Length;
            ExistsPrefix.CopyTo(_sendBuffer, 0);
            
            // Write key
            written += WriteBulkString(_sendBuffer.AsSpan()[written..], key);
            
            _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
            
            return ReadIntegerOptimized() > 0;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Del(string key)
    {
        lock (_syncLock)
        {
            var written = DelPrefix.Length;
            DelPrefix.CopyTo(_sendBuffer, 0);
            
            // Write key
            written += WriteBulkString(_sendBuffer.AsSpan()[written..], key);
            
            _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
            
            return ReadIntegerOptimized() > 0;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Incr(string key)
    {
        lock (_syncLock)
        {
            var written = IncrPrefix.Length;
            IncrPrefix.CopyTo(_sendBuffer, 0);
            
            // Write key
            written += WriteBulkString(_sendBuffer.AsSpan()[written..], key);
            
            _socket.Send(_sendBuffer, 0, written, SocketFlags.None);
            
            return ReadIntegerOptimized();
        }
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
        if (value < SmallNumbers.Length)
        {
            // Use pre-allocated string for small numbers
            var s = SmallNumbers[value];
            for (int i = 0; i < s.Length; i++)
            {
                span[i] = (byte)s[i];
            }
            return s.Length;
        }
        
        // For larger numbers, use stackalloc
        var temp = stackalloc byte[11];
        var tempLen = 0;
        var written = 0;
        
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
    private string? ReadBulkStringOptimized()
    {
        EnsureData();
        
        if (_receiveBuffer[_receiveOffset++] != '$')
            throw new InvalidOperationException("Expected bulk string");
        
        var length = ReadIntegerUntilCrlf();
        if (length == -1)
            return null;
            
        // For small strings, use StringBuilder to avoid allocation
        if (length <= 256)
        {
            _stringBuilder.Clear();
            for (int i = 0; i < length; i++)
            {
                EnsureData();
                _stringBuilder.Append((char)_receiveBuffer[_receiveOffset++]);
            }
            EnsureData();
            _receiveOffset += 2; // Skip \r\n
            return _stringBuilder.ToString();
        }
        
        // For larger strings, allocate normally
        var bytes = new byte[length];
        var bytesRead = 0;
        
        while (bytesRead < length)
        {
            EnsureData();
            var toRead = Math.Min((int)(length - bytesRead), _receiveCount - _receiveOffset);
            Buffer.BlockCopy(_receiveBuffer, _receiveOffset, bytes, bytesRead, toRead);
            _receiveOffset += toRead;
            bytesRead += toRead;
        }
        
        EnsureData();
        _receiveOffset += 2; // Skip \r\n
        
        return Encoding.UTF8.GetString(bytes);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadIntegerOptimized()
    {
        EnsureData();
        
        if (_receiveBuffer[_receiveOffset++] != ':')
            throw new InvalidOperationException("Expected integer");
        
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
    private void SkipLine()
    {
        while (true)
        {
            EnsureData();
            if (_receiveBuffer[_receiveOffset++] == '\r')
            {
                EnsureData();
                _receiveOffset++; // Skip \n
                return;
            }
        }
    }
    
    public void Dispose()
    {
        _socket?.Dispose();
    }
}