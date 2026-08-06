using Respire.Protocol;

namespace Respire.Commands;

/// <summary>
/// Pre-compiled RESP prefixes ("*&lt;arity&gt;\r\n$&lt;len&gt;\r\n&lt;VERB&gt;\r\n") for commands with
/// dynamic arguments. The prefix already encodes the array header and verb, so serializing a
/// command is one raw copy plus the argument bulk strings.
/// </summary>
public static class CommandPrefixes
{
    public static readonly byte[] Get = "*2\r\n$3\r\nGET\r\n"u8.ToArray();
    public static readonly byte[] Set = "*3\r\n$3\r\nSET\r\n"u8.ToArray();
    public static readonly byte[] Del = "*2\r\n$3\r\nDEL\r\n"u8.ToArray();
    public static readonly byte[] Exists = "*2\r\n$6\r\nEXISTS\r\n"u8.ToArray();
    public static readonly byte[] Expire = "*3\r\n$6\r\nEXPIRE\r\n"u8.ToArray();
    public static readonly byte[] Incr = "*2\r\n$4\r\nINCR\r\n"u8.ToArray();
    public static readonly byte[] Decr = "*2\r\n$4\r\nDECR\r\n"u8.ToArray();
    public static readonly byte[] Append = "*3\r\n$6\r\nAPPEND\r\n"u8.ToArray();
    public static readonly byte[] StrLen = "*2\r\n$6\r\nSTRLEN\r\n"u8.ToArray();
    public static readonly byte[] Ttl = "*2\r\n$3\r\nTTL\r\n"u8.ToArray();
    public static readonly byte[] HGet = "*3\r\n$4\r\nHGET\r\n"u8.ToArray();
    public static readonly byte[] HSet = "*4\r\n$4\r\nHSET\r\n"u8.ToArray();
    public static readonly byte[] HDel = "*3\r\n$4\r\nHDEL\r\n"u8.ToArray();
    public static readonly byte[] HExists = "*3\r\n$7\r\nHEXISTS\r\n"u8.ToArray();
    public static readonly byte[] HLen = "*2\r\n$4\r\nHLEN\r\n"u8.ToArray();
    public static readonly byte[] LPush = "*3\r\n$5\r\nLPUSH\r\n"u8.ToArray();
    public static readonly byte[] RPush = "*3\r\n$5\r\nRPUSH\r\n"u8.ToArray();
    public static readonly byte[] LPop = "*2\r\n$4\r\nLPOP\r\n"u8.ToArray();
    public static readonly byte[] RPop = "*2\r\n$4\r\nRPOP\r\n"u8.ToArray();
    public static readonly byte[] LLen = "*2\r\n$4\r\nLLEN\r\n"u8.ToArray();
    public static readonly byte[] SAdd = "*3\r\n$4\r\nSADD\r\n"u8.ToArray();
    public static readonly byte[] SRem = "*3\r\n$4\r\nSREM\r\n"u8.ToArray();
    public static readonly byte[] SIsMember = "*3\r\n$9\r\nSISMEMBER\r\n"u8.ToArray();
    public static readonly byte[] SCard = "*2\r\n$5\r\nSCARD\r\n"u8.ToArray();
    public static readonly byte[] Echo = "*2\r\n$4\r\nECHO\r\n"u8.ToArray();
    public static readonly byte[] Publish = "*3\r\n$7\r\nPUBLISH\r\n"u8.ToArray();
    public static readonly byte[] Subscribe = "*2\r\n$9\r\nSUBSCRIBE\r\n"u8.ToArray();
    public static readonly byte[] Unsubscribe = "*2\r\n$11\r\nUNSUBSCRIBE\r\n"u8.ToArray();
    public static readonly byte[] PSubscribe = "*2\r\n$10\r\nPSUBSCRIBE\r\n"u8.ToArray();
    public static readonly byte[] PUnsubscribe = "*2\r\n$12\r\nPUNSUBSCRIBE\r\n"u8.ToArray();
}

/// <summary>A fully pre-encoded command frame (PING, FLUSHDB, ...).</summary>
public readonly struct RawCommand(byte[] preEncoded) : IRespCommand
{
    public void Write(ref RespWriter writer) => writer.WriteRaw(preEncoded);
}

/// <summary>VERB key.</summary>
public readonly struct KeyCommand(byte[] prefix, string key) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw(prefix);
        writer.WriteBulkString(key);
    }
}

/// <summary>VERB key value.</summary>
public readonly struct KeyValueCommand(byte[] prefix, string key, string value) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw(prefix);
        writer.WriteBulkString(key);
        writer.WriteBulkString(value);
    }
}

/// <summary>VERB key &lt;integer&gt;.</summary>
public readonly struct KeyIntegerCommand(byte[] prefix, string key, long value) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw(prefix);
        writer.WriteBulkString(key);
        writer.WriteBulkInteger(value);
    }
}

/// <summary>VERB key field value (HSET, ...).</summary>
public readonly struct KeyFieldValueCommand(byte[] prefix, string key, string field, string value) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw(prefix);
        writer.WriteBulkString(key);
        writer.WriteBulkString(field);
        writer.WriteBulkString(value);
    }
}

/// <summary>VERB value (ECHO, SUBSCRIBE, ...).</summary>
public readonly struct SingleValueCommand(byte[] prefix, string value) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw(prefix);
        writer.WriteBulkString(value);
    }
}

/// <summary>HELLO 3 [AUTH username password] — RESP3 protocol negotiation.</summary>
public readonly struct HelloCommand(string? username, string? password) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        if (password is null)
        {
            writer.WriteRaw("*2\r\n$5\r\nHELLO\r\n$1\r\n3\r\n"u8);
            return;
        }

        writer.WriteRaw("*5\r\n$5\r\nHELLO\r\n$1\r\n3\r\n$4\r\nAUTH\r\n"u8);
        writer.WriteBulkString(username ?? "default");
        writer.WriteBulkString(password);
    }
}

/// <summary>AUTH [username] password — RESP2 authentication.</summary>
public readonly struct AuthCommand(string? username, string password) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        if (username is null)
        {
            writer.WriteRaw("*2\r\n$4\r\nAUTH\r\n"u8);
        }
        else
        {
            writer.WriteRaw("*3\r\n$4\r\nAUTH\r\n"u8);
            writer.WriteBulkString(username);
        }

        writer.WriteBulkString(password);
    }
}

/// <summary>CLIENT SETNAME name.</summary>
public readonly struct ClientSetNameCommand(string name) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw("*3\r\n$6\r\nCLIENT\r\n$7\r\nSETNAME\r\n"u8);
        writer.WriteBulkString(name);
    }
}
