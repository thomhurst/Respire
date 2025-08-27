using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Keva.Core.Protocol;

namespace Keva.Core.FastClient;

/// <summary>
/// Ultra-optimized RESP client using pre-compiled commands for maximum performance
/// Zero-allocation for command generation, direct socket operations
/// </summary>
public sealed class PreCompiledRespClient : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _receiveBuffer;
    private readonly object _syncLock = new();
    private int _receiveOffset;
    private int _receiveCount;
    
    public PreCompiledRespClient(string host, int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            SendBufferSize = 65536,
            ReceiveBufferSize = 65536
        };
        
        _receiveBuffer = new byte[65536];
        
        _socket.Connect(host, port);
    }
    
    // Zero-argument commands using pre-compiled byte arrays
    public string? Ping()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendPing(_socket);
            return ReadSimpleString();
        }
    }
    
    public string? RandomKey()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendRandomKey(_socket);
            return ReadBulkString();
        }
    }
    
    public long DbSize()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendDbSize(_socket);
            return ReadInteger();
        }
    }
    
    public string? Info()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendInfo(_socket);
            return ReadBulkString();
        }
    }
    
    public string? InfoServer()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendInfoServer(_socket);
            return ReadBulkString();
        }
    }
    
    public string? InfoMemory()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendInfoMemory(_socket);
            return ReadBulkString();
        }
    }
    
    public bool FlushDb()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendFlushDb(_socket);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public bool FlushAll()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendFlushAll(_socket);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public bool Save()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendSave(_socket);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public bool BgSave()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendBgSave(_socket);
            var response = ReadSimpleString();
            return response?.StartsWith("Background saving") == true;
        }
    }
    
    public long LastSave()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendLastSave(_socket);
            return ReadInteger();
        }
    }
    
    public bool Multi()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendMulti(_socket);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public string?[]? Exec()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendExec(_socket);
            return ReadArray();
        }
    }
    
    public bool Discard()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendDiscard(_socket);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public string?[] Role()
    {
        lock (_syncLock)
        {
            RespCommandMethods.SendRole(_socket);
            return ReadArray() ?? Array.Empty<string>();
        }
    }
    
    // Parameterized commands using pre-compiled templates
    public string? Get(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendGet(_socket, key);
            return ReadBulkString();
        }
    }
    
    public bool Set(string key, string value)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendSet(_socket, key, value);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public bool Del(params string[] keys)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendDel(_socket, keys);
            return ReadInteger() > 0;
        }
    }
    
    public long Exists(params string[] keys)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendExists(_socket, keys);
            return ReadInteger();
        }
    }
    
    public long Incr(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendIncr(_socket, key);
            return ReadInteger();
        }
    }
    
    public string?[] MGet(params string[] keys)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendMGet(_socket, keys);
            return ReadArray() ?? Array.Empty<string>();
        }
    }
    
    public string? Type(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendType(_socket, key);
            return ReadSimpleString();
        }
    }
    
    public long Ttl(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendTtl(_socket, key);
            return ReadInteger();
        }
    }
    
    public bool Expire(string key, int seconds)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendExpire(_socket, key, seconds);
            return ReadInteger() == 1;
        }
    }
    
    public string?[] Keys(string pattern)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendKeys(_socket, pattern);
            return ReadArray() ?? Array.Empty<string>();
        }
    }
    
    // List operations
    public long LPush(string key, params string[] values)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendLPush(_socket, key, values);
            return ReadInteger();
        }
    }
    
    public long LLen(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendLLen(_socket, key);
            return ReadInteger();
        }
    }
    
    public string?[] LRange(string key, int start, int stop)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendLRange(_socket, key, start, stop);
            return ReadArray() ?? Array.Empty<string>();
        }
    }
    
    // Set operations
    public long SAdd(string key, params string[] members)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendSAdd(_socket, key, members);
            return ReadInteger();
        }
    }
    
    public long SCard(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendSCard(_socket, key);
            return ReadInteger();
        }
    }
    
    public string?[] SMembers(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendSMembers(_socket, key);
            return ReadArray() ?? Array.Empty<string>();
        }
    }
    
    // Hash operations
    public bool HSet(string key, string field, string value)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendHSet(_socket, key, field, value);
            return ReadInteger() == 1;
        }
    }
    
    public string? HGet(string key, string field)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendHGet(_socket, key, field);
            return ReadBulkString();
        }
    }
    
    public string?[] HGetAll(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendHGetAll(_socket, key);
            return ReadArray() ?? Array.Empty<string>();
        }
    }
    
    // Sorted Set operations
    public long ZAdd(string key, double score, string member)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendZAdd(_socket, key, score, member);
            return ReadInteger();
        }
    }
    
    public long ZCard(string key)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendZCard(_socket, key);
            return ReadInteger();
        }
    }
    
    public string?[] ZRange(string key, int start, int stop, bool withScores = false)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendZRange(_socket, key, start, stop, withScores);
            return ReadArray() ?? Array.Empty<string>();
        }
    }
    
    // Connection commands
    public bool Auth(string password)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendAuth(_socket, password);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public bool Auth(string username, string password)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendAuth(_socket, username, password);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public bool Select(int database)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendSelect(_socket, database);
            var response = ReadSimpleString();
            return response == "OK";
        }
    }
    
    public string? Echo(string message)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendEcho(_socket, message);
            return ReadBulkString();
        }
    }
    
    // Pub/Sub commands
    public long Publish(string channel, string message)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendPublish(_socket, channel, message);
            return ReadInteger();
        }
    }
    
    // Generic command execution for advanced scenarios
    public void SendGeneric(ReadOnlySpan<byte> commandBytes, params string[] args)
    {
        lock (_syncLock)
        {
            RespCommandTemplates.SendGeneric(_socket, commandBytes, args);
        }
    }
    
    // Response reading methods (optimized for minimal allocations)
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
    private string?[]? ReadArray()
    {
        EnsureData();
        
        if (_receiveBuffer[_receiveOffset++] != '*')
        {
            throw new InvalidOperationException("Expected array");
        }

        var count = (int)ReadIntegerUntilCrlf();
        if (count == -1)
        {
            return null;
        }

        var result = new string?[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = ReadBulkString();
        }
        
        return result;
    }
    
    public void Dispose()
    {
        _socket?.Dispose();
    }
}