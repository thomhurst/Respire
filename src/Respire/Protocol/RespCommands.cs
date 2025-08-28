using System.Runtime.CompilerServices;
using System.Text;

namespace Respire.Protocol;

/// <summary>
/// Pre-compiled RESP protocol commands for maximum performance
/// Contains zero-allocation byte arrays for common Redis commands
/// </summary>
public static class RespCommands
{
    // Pre-allocated digit strings for small numbers (major performance optimization)
    private static readonly string[] SmallNumbers = new string[1000]; // 0-999 for most common cases
    
    // Common interned response strings
    public static readonly string PongResponse = "PONG";
    public static readonly string OkResponse = "OK";
    
    static RespCommands()
    {
        for (int i = 0; i < SmallNumbers.Length; i++)
        {
            SmallNumbers[i] = i.ToString();
        }
    }
    
    /// <summary>
    /// Gets a cached string representation of a number (0-999) to avoid allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetCachedNumber(int value)
    {
        return (value >= 0 && value < SmallNumbers.Length) ? SmallNumbers[value] : value.ToString();
    }
    // Zero-argument commands (complete RESP protocol ready)
    public static readonly byte[] Ping = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    public static readonly byte[] Quit = "*1\r\n$4\r\nQUIT\r\n"u8.ToArray();
    public static readonly byte[] RandomKey = "*1\r\n$9\r\nRANDOMKEY\r\n"u8.ToArray();
    public static readonly byte[] DbSize = "*1\r\n$6\r\nDBSIZE\r\n"u8.ToArray();
    public static readonly byte[] Info = "*1\r\n$4\r\nINFO\r\n"u8.ToArray();
    public static readonly byte[] Time = "*1\r\n$4\r\nTIME\r\n"u8.ToArray();
    public static readonly byte[] FlushDb = "*1\r\n$7\r\nFLUSHDB\r\n"u8.ToArray();
    public static readonly byte[] FlushAll = "*1\r\n$8\r\nFLUSHALL\r\n"u8.ToArray();
    public static readonly byte[] Save = "*1\r\n$4\r\nSAVE\r\n"u8.ToArray();
    public static readonly byte[] BgSave = "*1\r\n$6\r\nBGSAVE\r\n"u8.ToArray();
    public static readonly byte[] LastSave = "*1\r\n$8\r\nLASTSAVE\r\n"u8.ToArray();
    public static readonly byte[] Multi = "*1\r\n$5\r\nMULTI\r\n"u8.ToArray();
    public static readonly byte[] Exec = "*1\r\n$4\r\nEXEC\r\n"u8.ToArray();
    public static readonly byte[] Discard = "*1\r\n$7\r\nDISCARD\r\n"u8.ToArray();
    public static readonly byte[] Role = "*1\r\n$4\r\nROLE\r\n"u8.ToArray();
    
    // Help commands
    public static readonly byte[] CommandHelp = "*2\r\n$7\r\nCOMMAND\r\n$4\r\nHELP\r\n"u8.ToArray();
    public static readonly byte[] ClientHelp = "*2\r\n$6\r\nCLIENT\r\n$4\r\nHELP\r\n"u8.ToArray();
    public static readonly byte[] ConfigHelp = "*2\r\n$6\r\nCONFIG\r\n$4\r\nHELP\r\n"u8.ToArray();
    public static readonly byte[] FunctionHelp = "*2\r\n$8\r\nFUNCTION\r\n$4\r\nHELP\r\n"u8.ToArray();
    public static readonly byte[] MemoryHelp = "*2\r\n$6\r\nMEMORY\r\n$4\r\nHELP\r\n"u8.ToArray();
    public static readonly byte[] ModuleHelp = "*2\r\n$6\r\nMODULE\r\n$4\r\nHELP\r\n"u8.ToArray();
    public static readonly byte[] ScriptHelp = "*2\r\n$6\r\nSCRIPT\r\n$4\r\nHELP\r\n"u8.ToArray();
    public static readonly byte[] XInfoHelp = "*2\r\n$5\r\nXINFO\r\n$4\r\nHELP\r\n"u8.ToArray();
    
    // Info variants
    public static readonly byte[] InfoServer = "*2\r\n$4\r\nINFO\r\n$6\r\nserver\r\n"u8.ToArray();
    public static readonly byte[] InfoClients = "*2\r\n$4\r\nINFO\r\n$7\r\nclients\r\n"u8.ToArray();
    public static readonly byte[] InfoMemory = "*2\r\n$4\r\nINFO\r\n$6\r\nmemory\r\n"u8.ToArray();
    public static readonly byte[] InfoStats = "*2\r\n$4\r\nINFO\r\n$5\r\nstats\r\n"u8.ToArray();
    public static readonly byte[] InfoReplication = "*2\r\n$4\r\nINFO\r\n$11\r\nreplication\r\n"u8.ToArray();
    public static readonly byte[] InfoKeyspace = "*2\r\n$4\r\nINFO\r\n$8\r\nkeyspace\r\n"u8.ToArray();
    
    // List commands
    public static readonly byte[] CommandList = "*2\r\n$7\r\nCOMMAND\r\n$4\r\nLIST\r\n"u8.ToArray();
    public static readonly byte[] CommandCount = "*2\r\n$7\r\nCOMMAND\r\n$5\r\nCOUNT\r\n"u8.ToArray();
    public static readonly byte[] CommandDocs = "*2\r\n$7\r\nCOMMAND\r\n$4\r\nDOCS\r\n"u8.ToArray();
    public static readonly byte[] ClientList = "*2\r\n$6\r\nCLIENT\r\n$4\r\nLIST\r\n"u8.ToArray();
    public static readonly byte[] FunctionList = "*2\r\n$8\r\nFUNCTION\r\n$4\r\nLIST\r\n"u8.ToArray();
    public static readonly byte[] FunctionDump = "*2\r\n$8\r\nFUNCTION\r\n$4\r\nDUMP\r\n"u8.ToArray();
    public static readonly byte[] FunctionFlush = "*2\r\n$8\r\nFUNCTION\r\n$5\r\nFLUSH\r\n"u8.ToArray();
    public static readonly byte[] ModuleList = "*2\r\n$6\r\nMODULE\r\n$4\r\nLIST\r\n"u8.ToArray();
    public static readonly byte[] AclList = "*2\r\n$3\r\nACL\r\n$4\r\nLIST\r\n"u8.ToArray();
    
    // Command name byte arrays for parameterized commands (command part only)
    public static readonly byte[] Get = "$3\r\nGET\r\n"u8.ToArray();
    public static readonly byte[] Set = "$3\r\nSET\r\n"u8.ToArray();
    public static readonly byte[] Del = "$3\r\nDEL\r\n"u8.ToArray();
    public static readonly byte[] Exists = "$6\r\nEXISTS\r\n"u8.ToArray();
    public static readonly byte[] Incr = "$4\r\nINCR\r\n"u8.ToArray();
    public static readonly byte[] Decr = "$4\r\nDECR\r\n"u8.ToArray();
    public static readonly byte[] IncrBy = "$6\r\nINCRBY\r\n"u8.ToArray();
    public static readonly byte[] DecrBy = "$6\r\nDECRBY\r\n"u8.ToArray();
    public static readonly byte[] MGet = "$4\r\nMGET\r\n"u8.ToArray();
    public static readonly byte[] MSet = "$4\r\nMSET\r\n"u8.ToArray();
    public static readonly byte[] SetEx = "$5\r\nSETEX\r\n"u8.ToArray();
    public static readonly byte[] SetNx = "$5\r\nSETNX\r\n"u8.ToArray();
    public static readonly byte[] GetSet = "$6\r\nGETSET\r\n"u8.ToArray();
    public static readonly byte[] Append = "$6\r\nAPPEND\r\n"u8.ToArray();
    public static readonly byte[] StrLen = "$6\r\nSTRLEN\r\n"u8.ToArray();
    
    // List commands
    public static readonly byte[] LPush = "$5\r\nLPUSH\r\n"u8.ToArray();
    public static readonly byte[] RPush = "$5\r\nRPUSH\r\n"u8.ToArray();
    public static readonly byte[] LPop = "$4\r\nLPOP\r\n"u8.ToArray();
    public static readonly byte[] RPop = "$4\r\nRPOP\r\n"u8.ToArray();
    public static readonly byte[] LLen = "$4\r\nLLEN\r\n"u8.ToArray();
    public static readonly byte[] LIndex = "$6\r\nLINDEX\r\n"u8.ToArray();
    public static readonly byte[] LRange = "$6\r\nLRANGE\r\n"u8.ToArray();
    public static readonly byte[] LSet = "$4\r\nLSET\r\n"u8.ToArray();
    public static readonly byte[] LRem = "$4\r\nLREM\r\n"u8.ToArray();
    public static readonly byte[] LTrim = "$5\r\nLTRIM\r\n"u8.ToArray();
    public static readonly byte[] LInsert = "$7\r\nLINSERT\r\n"u8.ToArray();
    
    // Set commands
    public static readonly byte[] SAdd = "$4\r\nSADD\r\n"u8.ToArray();
    public static readonly byte[] SRem = "$4\r\nSREM\r\n"u8.ToArray();
    public static readonly byte[] SCard = "$5\r\nSCARD\r\n"u8.ToArray();
    public static readonly byte[] SIsMember = "$9\r\nSISMEMBER\r\n"u8.ToArray();
    public static readonly byte[] SMembers = "$8\r\nSMEMBERS\r\n"u8.ToArray();
    public static readonly byte[] SRandMember = "$11\r\nSRANDMEMBER\r\n"u8.ToArray();
    public static readonly byte[] SPop = "$4\r\nSPOP\r\n"u8.ToArray();
    public static readonly byte[] SMove = "$5\r\nSMOVE\r\n"u8.ToArray();
    public static readonly byte[] SInter = "$6\r\nSINTER\r\n"u8.ToArray();
    public static readonly byte[] SUnion = "$6\r\nSUNION\r\n"u8.ToArray();
    public static readonly byte[] SDiff = "$5\r\nSDIFF\r\n"u8.ToArray();
    
    // Hash commands
    public static readonly byte[] HSet = "$4\r\nHSET\r\n"u8.ToArray();
    public static readonly byte[] HGet = "$4\r\nHGET\r\n"u8.ToArray();
    public static readonly byte[] HMSet = "$5\r\nHMSET\r\n"u8.ToArray();
    public static readonly byte[] HMGet = "$5\r\nHMGET\r\n"u8.ToArray();
    public static readonly byte[] HGetAll = "$7\r\nHGETALL\r\n"u8.ToArray();
    public static readonly byte[] HDel = "$4\r\nHDEL\r\n"u8.ToArray();
    public static readonly byte[] HExists = "$7\r\nHEXISTS\r\n"u8.ToArray();
    public static readonly byte[] HLen = "$4\r\nHLEN\r\n"u8.ToArray();
    public static readonly byte[] HKeys = "$5\r\nHKEYS\r\n"u8.ToArray();
    public static readonly byte[] HVals = "$5\r\nHVALS\r\n"u8.ToArray();
    public static readonly byte[] HIncrBy = "$7\r\nHINCRBY\r\n"u8.ToArray();
    public static readonly byte[] HRandField = "$10\r\nHRANDFIELD\r\n"u8.ToArray();
    
    // Sorted Set commands
    public static readonly byte[] ZAdd = "$4\r\nZADD\r\n"u8.ToArray();
    public static readonly byte[] ZRem = "$4\r\nZREM\r\n"u8.ToArray();
    public static readonly byte[] ZScore = "$6\r\nZSCORE\r\n"u8.ToArray();
    public static readonly byte[] ZCard = "$5\r\nZCARD\r\n"u8.ToArray();
    public static readonly byte[] ZCount = "$6\r\nZCOUNT\r\n"u8.ToArray();
    public static readonly byte[] ZRange = "$6\r\nZRANGE\r\n"u8.ToArray();
    public static readonly byte[] ZRank = "$5\r\nZRANK\r\n"u8.ToArray();
    public static readonly byte[] ZRevRank = "$8\r\nZREVRANK\r\n"u8.ToArray();
    public static readonly byte[] ZIncrBy = "$7\r\nZINCRBY\r\n"u8.ToArray();
    public static readonly byte[] ZUnion = "$6\r\nZUNION\r\n"u8.ToArray();
    public static readonly byte[] ZMPop = "$5\r\nZMPOP\r\n"u8.ToArray();
    
    // Key commands
    public static readonly byte[] Type = "$4\r\nTYPE\r\n"u8.ToArray();
    public static readonly byte[] Ttl = "$3\r\nTTL\r\n"u8.ToArray();
    public static readonly byte[] PTtl = "$4\r\nPTTL\r\n"u8.ToArray();
    public static readonly byte[] Expire = "$6\r\nEXPIRE\r\n"u8.ToArray();
    public static readonly byte[] ExpireAt = "$8\r\nEXPIREAT\r\n"u8.ToArray();
    public static readonly byte[] PExpire = "$7\r\nPEXPIRE\r\n"u8.ToArray();
    public static readonly byte[] PExpireAt = "$9\r\nPEXPIREAT\r\n"u8.ToArray();
    public static readonly byte[] Persist = "$7\r\nPERSIST\r\n"u8.ToArray();
    public static readonly byte[] Keys = "$4\r\nKEYS\r\n"u8.ToArray();
    public static readonly byte[] Scan = "$4\r\nSCAN\r\n"u8.ToArray();
    public static readonly byte[] Rename = "$6\r\nRENAME\r\n"u8.ToArray();
    public static readonly byte[] RenameNx = "$8\r\nRENAMENX\r\n"u8.ToArray();
    
    // Pub/Sub commands
    public static readonly byte[] Publish = "$7\r\nPUBLISH\r\n"u8.ToArray();
    public static readonly byte[] Subscribe = "$9\r\nSUBSCRIBE\r\n"u8.ToArray();
    public static readonly byte[] Unsubscribe = "$11\r\nUNSUBSCRIBE\r\n"u8.ToArray();
    public static readonly byte[] PSubscribe = "$10\r\nPSUBSCRIBE\r\n"u8.ToArray();
    public static readonly byte[] PUnsubscribe = "$12\r\nPUNSUBSCRIBE\r\n"u8.ToArray();
    
    // Connection commands
    public static readonly byte[] Auth = "$4\r\nAUTH\r\n"u8.ToArray();
    public static readonly byte[] Select = "$6\r\nSELECT\r\n"u8.ToArray();
    public static readonly byte[] Echo = "$4\r\nECHO\r\n"u8.ToArray();
    
    // Blocking commands
    public static readonly byte[] BLPop = "$5\r\nBLPOP\r\n"u8.ToArray();
    public static readonly byte[] BRPop = "$5\r\nBRPOP\r\n"u8.ToArray();
    public static readonly byte[] BLMPop = "$6\r\nBLMPOP\r\n"u8.ToArray();
    public static readonly byte[] BZMPop = "$6\r\nBZMPOP\r\n"u8.ToArray();
    public static readonly byte[] BRPopLPush = "$10\r\nBRPOPLPUSH\r\n"u8.ToArray();
    
    // Stream commands  
    public static readonly byte[] XAdd = "$4\r\nXADD\r\n"u8.ToArray();
    public static readonly byte[] XRead = "$5\r\nXREAD\r\n"u8.ToArray();
    public static readonly byte[] XLen = "$4\r\nXLEN\r\n"u8.ToArray();
    public static readonly byte[] XRange = "$6\r\nXRANGE\r\n"u8.ToArray();
    public static readonly byte[] XRevRange = "$9\r\nXREVRANGE\r\n"u8.ToArray();
    public static readonly byte[] XDel = "$4\r\nXDEL\r\n"u8.ToArray();
    public static readonly byte[] XTrim = "$5\r\nXTRIM\r\n"u8.ToArray();
    
    // Script commands
    public static readonly byte[] Eval = "$4\r\nEVAL\r\n"u8.ToArray();
    public static readonly byte[] EvalSha = "$7\r\nEVALSHA\r\n"u8.ToArray();
    public static readonly byte[] ScriptLoad = "$6\r\nSCRIPT\r\n$4\r\nLOAD\r\n"u8.ToArray();
    public static readonly byte[] ScriptExists = "$6\r\nSCRIPT\r\n$6\r\nEXISTS\r\n"u8.ToArray();
    public static readonly byte[] ScriptFlush = "$6\r\nSCRIPT\r\n$5\r\nFLUSH\r\n"u8.ToArray();
    public static readonly byte[] ScriptKill = "$6\r\nSCRIPT\r\n$4\r\nKILL\r\n"u8.ToArray();
    
    // Helper methods for building commands with parameters
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteArrayHeader(Span<byte> buffer, ref int offset, int arrayLength)
    {
        buffer[offset++] = (byte)'*';
        WriteInteger(buffer, ref offset, arrayLength);
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteBulkString(Span<byte> buffer, ref int offset, ReadOnlySpan<byte> value)
    {
        buffer[offset++] = (byte)'$';
        WriteInteger(buffer, ref offset, value.Length);
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
        value.CopyTo(buffer[offset..]);
        offset += value.Length;
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteBulkString(Span<byte> buffer, ref int offset, string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        buffer[offset++] = (byte)'$';
        WriteInteger(buffer, ref offset, bytes);
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
        offset += Encoding.UTF8.GetBytes(value, buffer[offset..]);
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCommandBytes(Span<byte> buffer, ref int offset, ReadOnlySpan<byte> commandBytes)
    {
        commandBytes.CopyTo(buffer[offset..]);
        offset += commandBytes.Length;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInteger(Span<byte> buffer, ref int offset, int value)
    {
        if (value == 0)
        {
            buffer[offset++] = (byte)'0';
            return;
        }
        
        var start = offset;
        if (value < 0)
        {
            buffer[offset++] = (byte)'-';
            value = -value;
        }
        
        // Write digits in reverse
        var digitStart = offset;
        while (value > 0)
        {
            buffer[offset++] = (byte)('0' + (value % 10));
            value /= 10;
        }
        
        // Reverse the digits
        var left = digitStart;
        var right = offset - 1;
        while (left < right)
        {
            (buffer[left], buffer[right]) = (buffer[right], buffer[left]);
            left++;
            right--;
        }
    }
    
    // Common command builders for frequently used patterns
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildGetCommand(Span<byte> buffer, string key)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 2);
        WriteCommandBytes(buffer, ref offset, Get);
        WriteBulkString(buffer, ref offset, key);
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildSetCommand(Span<byte> buffer, string key, string value)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 3);
        WriteCommandBytes(buffer, ref offset, Set);
        WriteBulkString(buffer, ref offset, key);
        WriteBulkString(buffer, ref offset, value);
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildDelCommand(Span<byte> buffer, params string[] keys)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, keys.Length + 1);
        WriteCommandBytes(buffer, ref offset, Del);
        foreach (var key in keys)
        {
            WriteBulkString(buffer, ref offset, key);
        }
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildExistsCommand(Span<byte> buffer, params string[] keys)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, keys.Length + 1);
        WriteCommandBytes(buffer, ref offset, Exists);
        foreach (var key in keys)
        {
            WriteBulkString(buffer, ref offset, key);
        }
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildIncrCommand(Span<byte> buffer, string key)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 2);
        WriteCommandBytes(buffer, ref offset, Incr);
        WriteBulkString(buffer, ref offset, key);
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildMGetCommand(Span<byte> buffer, params string[] keys)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, keys.Length + 1);
        WriteCommandBytes(buffer, ref offset, MGet);
        foreach (var key in keys)
        {
            WriteBulkString(buffer, ref offset, key);
        }
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildLPushCommand(Span<byte> buffer, string key, params string[] values)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, values.Length + 2);
        WriteCommandBytes(buffer, ref offset, LPush);
        WriteBulkString(buffer, ref offset, key);
        foreach (var value in values)
        {
            WriteBulkString(buffer, ref offset, value);
        }
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildHSetCommand(Span<byte> buffer, string key, string field, string value)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 4);
        WriteCommandBytes(buffer, ref offset, HSet);
        WriteBulkString(buffer, ref offset, key);
        WriteBulkString(buffer, ref offset, field);
        WriteBulkString(buffer, ref offset, value);
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildHGetCommand(Span<byte> buffer, string key, string field)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 3);
        WriteCommandBytes(buffer, ref offset, HGet);
        WriteBulkString(buffer, ref offset, key);
        WriteBulkString(buffer, ref offset, field);
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildZAddCommand(Span<byte> buffer, string key, double score, string member)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 4);
        WriteCommandBytes(buffer, ref offset, ZAdd);
        WriteBulkString(buffer, ref offset, key);
        WriteDoubleAsBulkString(buffer, ref offset, score);
        WriteBulkString(buffer, ref offset, member);
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildGenericCommand(Span<byte> buffer, ReadOnlySpan<byte> command, params string[] args)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, args.Length + 1);
        WriteCommandBytes(buffer, ref offset, command);
        foreach (var arg in args)
        {
            WriteBulkString(buffer, ref offset, arg);
        }
        return offset;
    }
    
    /// <summary>
    /// Builds EXPIRE command with zero allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildExpireCommand(Span<byte> buffer, string key, int seconds)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 3);
        WriteCommandBytes(buffer, ref offset, "EXPIRE"u8);
        WriteBulkString(buffer, ref offset, key);
        WriteIntegerAsBulkString(buffer, ref offset, seconds);
        return offset;
    }
    
    /// <summary>
    /// Builds TTL command with zero allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildTtlCommand(Span<byte> buffer, string key)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 2);
        WriteCommandBytes(buffer, ref offset, "TTL"u8);
        WriteBulkString(buffer, ref offset, key);
        return offset;
    }
    
    /// <summary>
    /// Builds SADD command with zero allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildSAddCommand(Span<byte> buffer, string key, string member)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 3);
        WriteCommandBytes(buffer, ref offset, "SADD"u8);
        WriteBulkString(buffer, ref offset, key);
        WriteBulkString(buffer, ref offset, member);
        return offset;
    }
    
    /// <summary>
    /// Builds SREM command with zero allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildSRemCommand(Span<byte> buffer, string key, string member)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 3);
        WriteCommandBytes(buffer, ref offset, "SREM"u8);
        WriteBulkString(buffer, ref offset, key);
        WriteBulkString(buffer, ref offset, member);
        return offset;
    }
    
    /// <summary>
    /// Builds RPOP command with zero allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BuildRPopCommand(Span<byte> buffer, string key)
    {
        var offset = 0;
        WriteArrayHeader(buffer, ref offset, 2);
        WriteCommandBytes(buffer, ref offset, "RPOP"u8);
        WriteBulkString(buffer, ref offset, key);
        return offset;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteDoubleAsBulkString(Span<byte> buffer, ref int offset, double value)
    {
        // Use stack allocation for the double string representation
        Span<char> temp = stackalloc char[32]; // Sufficient for most double values
        if (value.TryFormat(temp, out int charsWritten, "G17", System.Globalization.CultureInfo.InvariantCulture))
        {
            // Write $<length>\r\n
            buffer[offset++] = (byte)'$';
            WriteIntegerDirectly(buffer, ref offset, charsWritten);
            buffer[offset++] = (byte)'\r';
            buffer[offset++] = (byte)'\n';
            
            // Write the double value as ASCII bytes
            for (int i = 0; i < charsWritten; i++)
            {
                buffer[offset++] = (byte)temp[i];
            }
            buffer[offset++] = (byte)'\r';
            buffer[offset++] = (byte)'\n';
        }
        else
        {
            // Fallback to ToString() for edge cases
            WriteBulkString(buffer, ref offset, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteIntegerAsBulkString(Span<byte> buffer, ref int offset, int value)
    {
        // Calculate digits needed for the integer
        var digitCount = value == 0 ? 1 : (value < 0 ? 1 : 0) + (int)Math.Log10(Math.Abs(value)) + 1;
        
        // Write $<length>\r\n
        buffer[offset++] = (byte)'$';
        WriteIntegerDirectly(buffer, ref offset, digitCount);
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
        
        // Write the integer value directly
        WriteIntegerDirectly(buffer, ref offset, value);
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteIntegerDirectly(Span<byte> buffer, ref int offset, int value)
    {
        // Use cached string for small numbers for maximum performance
        if (value >= 0 && value < SmallNumbers.Length)
        {
            var cachedStr = SmallNumbers[value];
            for (int i = 0; i < cachedStr.Length; i++)
            {
                buffer[offset++] = (byte)cachedStr[i];
            }
            return;
        }
        
        if (value == 0)
        {
            buffer[offset++] = (byte)'0';
            return;
        }
        
        if (value < 0)
        {
            buffer[offset++] = (byte)'-';
            value = -value;
        }
        
        // Use stack allocation for temporary storage
        Span<byte> temp = stackalloc byte[11]; // Max digits for int32
        var tempIndex = 0;
        
        while (value > 0)
        {
            temp[tempIndex++] = (byte)('0' + (value % 10));
            value /= 10;
        }
        
        // Write digits in reverse order
        while (tempIndex > 0)
        {
            buffer[offset++] = temp[--tempIndex];
        }
    }
}