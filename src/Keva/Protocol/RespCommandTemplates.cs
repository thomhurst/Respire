using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Keva.Protocol;

/// <summary>
/// High-performance command templates for parameterized Redis commands
/// Uses pre-compiled command parts with efficient parameter injection
/// </summary>
public static class RespCommandTemplates
{
    // Common buffer size for most commands
    private const int DefaultBufferSize = 512;
    private const int LargeBufferSize = 2048;
    
    /// <summary>
    /// Executes GET command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendGet(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGetCommand(buffer, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes SET command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendSet(Socket socket, string key, string value)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildSetCommand(buffer, key, value);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes DEL command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendDel(Socket socket, params string[] keys)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildDelCommand(buffer, keys);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes EXISTS command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendExists(Socket socket, params string[] keys)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildExistsCommand(buffer, keys);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes INCR command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendIncr(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildIncrCommand(buffer, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes MGET command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendMGet(Socket socket, params string[] keys)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildMGetCommand(buffer, keys);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes LPUSH command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendLPush(Socket socket, string key, params string[] values)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildLPushCommand(buffer, key, values);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes HSET command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendHSet(Socket socket, string key, string field, string value)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildHSetCommand(buffer, key, field, value);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes HGET command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendHGet(Socket socket, string key, string field)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildHGetCommand(buffer, key, field);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes ZADD command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendZAdd(Socket socket, string key, double score, string member)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildZAddCommand(buffer, key, score, member);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes TYPE command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendType(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Type, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes TTL command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendTtl(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Ttl, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes EXPIRE command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendExpire(Socket socket, string key, int seconds)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Expire, key, seconds.ToString());
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes KEYS command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendKeys(Socket socket, string pattern)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Keys, pattern);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes LLEN command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendLLen(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.LLen, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes LRANGE command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendLRange(Socket socket, string key, int start, int stop)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.LRange, key, start.ToString(), stop.ToString());
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes SADD command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendSAdd(Socket socket, string key, params string[] members)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var args = new string[members.Length + 1];
        args[0] = key;
        Array.Copy(members, 0, args, 1, members.Length);
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.SAdd, args);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes SCARD command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendSCard(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.SCard, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes SMEMBERS command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendSMembers(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.SMembers, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes HGETALL command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendHGetAll(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.HGetAll, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes ZCARD command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendZCard(Socket socket, string key)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.ZCard, key);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes ZRANGE command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendZRange(Socket socket, string key, int start, int stop, bool withScores = false)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        int length;
        if (withScores)
        {
            length = RespCommands.BuildGenericCommand(buffer, RespCommands.ZRange, key, start.ToString(), stop.ToString(), "WITHSCORES");
        }
        else
        {
            length = RespCommands.BuildGenericCommand(buffer, RespCommands.ZRange, key, start.ToString(), stop.ToString());
        }
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes AUTH command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendAuth(Socket socket, string password)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Auth, password);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes AUTH command with username and password
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendAuth(Socket socket, string username, string password)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Auth, username, password);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes SELECT command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendSelect(Socket socket, int database)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Select, database.ToString());
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes ECHO command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendEcho(Socket socket, string message)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Echo, message);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes PUBLISH command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendPublish(Socket socket, string channel, string message)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Publish, channel, message);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes SUBSCRIBE command with pre-compiled template
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendSubscribe(Socket socket, params string[] channels)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, RespCommands.Subscribe, channels);
        socket.Send(buffer[..length]);
    }
    
    /// <summary>
    /// Executes generic command with custom command bytes and arguments
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendGeneric(Socket socket, ReadOnlySpan<byte> commandBytes, params string[] args)
    {
        Span<byte> buffer = stackalloc byte[LargeBufferSize];
        var length = RespCommands.BuildGenericCommand(buffer, commandBytes, args);
        socket.Send(buffer[..length]);
    }
}