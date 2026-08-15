using Respire.Protocol;

namespace Respire.Commands;

/// <summary>A fully pre-encoded command frame (PING, FLUSHDB, ...).</summary>
internal readonly struct RawCommand(byte[] preEncoded) : IRespCommand
{
    public void Write(ref RespWriter writer) => writer.WriteRaw(preEncoded);
}

/// <summary>HELLO 3 [AUTH username password] — RESP3 protocol negotiation.</summary>
internal readonly struct HelloCommand(string? username, string? password) : IRespCommand
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
internal readonly struct AuthCommand(string? username, string password) : IRespCommand
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
internal readonly struct ClientSetNameCommand(string name) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw("*3\r\n$6\r\nCLIENT\r\n$7\r\nSETNAME\r\n"u8);
        writer.WriteBulkString(name);
    }
}

/// <summary>CLIENT TRACKING ON OPTIN.</summary>
internal readonly struct ClientTrackingCommand : IRespCommand
{
    public void Write(ref RespWriter writer)
        => writer.WriteRaw("*4\r\n$6\r\nCLIENT\r\n$8\r\nTRACKING\r\n$2\r\nON\r\n$5\r\nOPTIN\r\n"u8);
}

/// <summary>CLIENT CACHING YES.</summary>
internal readonly struct ClientCachingCommand : IRespCommand
{
    public void Write(ref RespWriter writer)
        => writer.WriteRaw("*3\r\n$6\r\nCLIENT\r\n$7\r\nCACHING\r\n$3\r\nYES\r\n"u8);
}

/// <summary>CLIENT ID.</summary>
internal readonly struct ClientIdCommand : IRespCommand
{
    public void Write(ref RespWriter writer)
        => writer.WriteRaw("*2\r\n$6\r\nCLIENT\r\n$2\r\nID\r\n"u8);
}

/// <summary>CLIENT KILL ID id [SKIPME yes].</summary>
internal readonly struct ClientKillIdCommand(long id, bool skipMe = false) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        if (skipMe)
        {
            writer.WriteRaw("*6\r\n$6\r\nCLIENT\r\n$4\r\nKILL\r\n$2\r\nID\r\n"u8);
        }
        else
        {
            writer.WriteRaw("*4\r\n$6\r\nCLIENT\r\n$4\r\nKILL\r\n$2\r\nID\r\n"u8);
        }

        writer.WriteBulkInteger(id);
        if (skipMe)
        {
            writer.WriteRaw("$6\r\nSKIPME\r\n$3\r\nyes\r\n"u8);
        }
    }
}

/// <summary>SELECT database.</summary>
internal readonly struct SelectCommand(int database) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw("*2\r\n$6\r\nSELECT\r\n"u8);
        writer.WriteBulkInteger(database);
    }
}
