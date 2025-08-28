using System.Text;

namespace Respire.Protocol;

/// <summary>
/// Provides utility methods for parsing RESP commands
/// </summary>
public static class RespCommandParser
{
    
    /// <summary>
    /// Extracts the command name from a RESP-formatted command
    /// </summary>
    /// <param name="command">The RESP command bytes</param>
    /// <returns>The command name, or empty string if unable to parse</returns>
    public static string GetCommandName(this ReadOnlySpan<byte> command)
    {
        // RESP commands are typically formatted as:
        // *3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n
        // We need to extract "SET" from this
        
        if (command.Length == 0)
        {
            return string.Empty;
        }

        // Check if it's an array (starts with *)
        if (command[0] != (byte)'*')
        {
            // Might be a simple command, try to extract directly
            return ExtractSimpleCommand(command);
        }
        
        // Skip array header (*N\r\n)
        var index = command.IndexOf(RespConstants.CRLF);
        if (index == -1)
        {
            return string.Empty;
        }

        index += 2; // Skip \r\n
        
        // Now we should be at the first bulk string ($N\r\n)
        if (index >= command.Length || command[index] != (byte)'$')
        {
            return string.Empty;
        }

        // Find the length
        var lengthStart = index + 1;
        var lengthEnd = command.Slice(lengthStart).IndexOf(RespConstants.CRLF);
        if (lengthEnd == -1)
        {
            return string.Empty;
        }

        // Parse the length
        var lengthSpan = command.Slice(lengthStart, lengthEnd);
        if (!TryParseInt(lengthSpan, out var length) || length <= 0)
        {
            return string.Empty;
        }

        // Skip to the actual command string
        index = lengthStart + lengthEnd + 2; // Skip length and \r\n
        
        if (index + length > command.Length)
        {
            return string.Empty;
        }

        // Extract the command name
        var commandName = command.Slice(index, length);
        return Encoding.UTF8.GetString(commandName);
    }
    
    /// <summary>
    /// Extracts the command name from a RESP-formatted command
    /// </summary>
    /// <param name="command">The RESP command bytes</param>
    /// <returns>The command name, or empty string if unable to parse</returns>
    public static string GetCommandName(this ReadOnlyMemory<byte> command)
    {
        return GetCommandName(command.Span);
    }
    
    /// <summary>
    /// Parses command and arguments from a RESP-formatted command
    /// </summary>
    /// <param name="command">The RESP command bytes</param>
    /// <param name="commandName">The extracted command name</param>
    /// <param name="arguments">The extracted arguments</param>
    /// <returns>True if successfully parsed, false otherwise</returns>
    public static bool TryParseCommand(
        this ReadOnlySpan<byte> command,
        out string commandName,
        out string[] arguments)
    {
        commandName = string.Empty;
        arguments = Array.Empty<string>();
        
        if (command.Length == 0)
        {
            return false;
        }

        // Check if it's an array (starts with *)
        if (command[0] != (byte)'*')
        {
            return false;
        }

        // Parse array count
        var index = 1;
        var countEnd = command.Slice(index).IndexOf(RespConstants.CRLF);
        if (countEnd == -1)
        {
            return false;
        }

        if (!TryParseInt(command.Slice(index, countEnd), out var count) || count <= 0)
        {
            return false;
        }

        index += countEnd + 2; // Skip count and \r\n
        
        var parts = new string[count];
        
        for (int i = 0; i < count; i++)
        {
            if (index >= command.Length)
            {
                return false;
            }

            // Each part should be a bulk string
            if (command[index] != (byte)'$')
            {
                return false;
            }

            // Parse length
            var lengthStart = index + 1;
            var lengthEnd = command.Slice(lengthStart).IndexOf(RespConstants.CRLF);
            if (lengthEnd == -1)
            {
                return false;
            }

            if (!TryParseInt(command.Slice(lengthStart, lengthEnd), out var length))
            {
                return false;
            }

            index = lengthStart + lengthEnd + 2; // Skip length and \r\n
            
            if (length == -1)
            {
                // Null bulk string
                parts[i] = null;
            }
            else
            {
                if (index + length > command.Length)
                {
                    return false;
                }

                parts[i] = Encoding.UTF8.GetString(command.Slice(index, length));
                index += length + 2; // Skip string and \r\n
            }
        }
        
        if (parts.Length > 0)
        {
            commandName = parts[0] ?? string.Empty;
            if (parts.Length > 1)
            {
                arguments = new string[parts.Length - 1];
                Array.Copy(parts, 1, arguments, 0, arguments.Length);
            }
        }
        
        return true;
    }
    
    /// <summary>
    /// Checks if a command is idempotent (safe to retry)
    /// </summary>
    public static bool IsIdempotentCommand(string commandName)
    {
        // Redis/Valkey idempotent (read-only or safe) commands
        return commandName?.ToUpperInvariant() switch
        {
            // String operations (read-only)
            "GET" or "MGET" or "STRLEN" or "GETRANGE" or "GETBIT" => true,
            
            // Hash operations (read-only)
            "HGET" or "HMGET" or "HGETALL" or "HKEYS" or "HVALS" or 
            "HEXISTS" or "HLEN" or "HSCAN" => true,
            
            // List operations (read-only)
            "LRANGE" or "LINDEX" or "LLEN" => true,
            
            // Set operations (read-only)
            "SCARD" or "SISMEMBER" or "SMEMBERS" or "SRANDMEMBER" or
            "SSCAN" or "SINTER" or "SUNION" or "SDIFF" => true,
            
            // Sorted set operations (read-only)
            "ZCARD" or "ZCOUNT" or "ZRANGE" or "ZRANGEBYSCORE" or 
            "ZRANK" or "ZREVRANK" or "ZSCORE" or "ZSCAN" => true,
            
            // Key operations (read-only)
            "EXISTS" or "TYPE" or "TTL" or "PTTL" or "KEYS" or 
            "SCAN" or "RANDOMKEY" => true,
            
            // Server operations (read-only)
            "PING" or "ECHO" or "TIME" or "DBSIZE" or "INFO" or
            "CONFIG GET" or "CLIENT LIST" or "CLIENT GETNAME" => true,
            
            // Connection operations (safe)
            "AUTH" or "SELECT" or "QUIT" => true,
            
            _ => false
        };
    }
    
    private static string ExtractSimpleCommand(ReadOnlySpan<byte> command)
    {
        // For simple commands like "PING\r\n"
        var end = command.IndexOf(RespConstants.CRLF);
        if (end == -1)
        {
            // No CRLF, might be just the command
            end = command.Length;
        }
        
        return Encoding.UTF8.GetString(command.Slice(0, end));
    }
    
    private static bool TryParseInt(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        if (bytes.Length == 0)
        {
            return false;
        }

        var negative = false;
        var start = 0;
        
        if (bytes[0] == (byte)'-')
        {
            negative = true;
            start = 1;
        }
        
        for (int i = start; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b < (byte)'0' || b > (byte)'9')
            {
                return false;
            }

            value = value * 10 + (b - (byte)'0');
        }
        
        if (negative)
        {
            value = -value;
        }

        return true;
    }
}