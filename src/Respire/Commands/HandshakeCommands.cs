using Respire.Protocol;

namespace Respire.Commands;

/// <summary>A fully pre-encoded command frame (PING, FLUSHDB, ...).</summary>
internal readonly struct RawCommand(byte[] preEncoded, ReadCommandKind readKind = ReadCommandKind.None) : IRespCommand
{
    public int GetWriteSizeHint() => preEncoded.Length;
    public ReadCommandKind ReadKind => readKind;
    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => ClientCacheCommandMetadata.Get(operation);

    public RespireCacheMutation GetCacheMutation(string operation)
        => ReferenceEquals(preEncoded, RespCommands.Ping) || ReferenceEquals(preEncoded, RespCommands.RandomKey)
            || ReferenceEquals(preEncoded, RespCommands.DbSize) || ReferenceEquals(preEncoded, RespCommands.Info)
            || ReferenceEquals(preEncoded, RespCommands.Time) || ReferenceEquals(preEncoded, RespCommands.LastSave)
            || ReferenceEquals(preEncoded, RespCommands.Role)
            ? RespireCacheMutation.ReadOnly : RespireCommands.GetCacheMutation(operation);

    public void Write(ref RespWriter writer) => writer.WriteRaw(preEncoded);
}

/// <summary>HELLO 3 [AUTH username password] — RESP3 protocol negotiation.</summary>
internal readonly struct HelloCommand(string? username, string? password) : IConnectionProtocolCommand
{
    public int GetWriteSizeHint() => password is null ? "*2\r\n$5\r\nHELLO\r\n$1\r\n3\r\n"u8.Length
        : CommandWriteSizeHint.For("*5\r\n$5\r\nHELLO\r\n$1\r\n3\r\n$4\r\nAUTH\r\n"u8.Length,
            ((RespireValue)(username ?? "default")).GetWriteSizeHint(), ((RespireValue)password).GetWriteSizeHint());
    public ReadCommandKind ReadKind => ReadCommandKind.None;

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
internal readonly struct AuthCommand(string? username, string password) : IConnectionProtocolCommand
{
    public int GetWriteSizeHint() => CommandWriteSizeHint.For("*2\r\n$4\r\nAUTH\r\n"u8.Length,
        username is null ? 0 : ((RespireValue)username).GetWriteSizeHint(), ((RespireValue)password).GetWriteSizeHint());
    public ReadCommandKind ReadKind => ReadCommandKind.None;

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
internal readonly struct ClientSetNameCommand(string name) : IConnectionProtocolCommand
{
    public int GetWriteSizeHint() => CommandWriteSizeHint.For("*3\r\n$6\r\nCLIENT\r\n$7\r\nSETNAME\r\n"u8.Length,
        ((RespireValue)name).GetWriteSizeHint());
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw("*3\r\n$6\r\nCLIENT\r\n$7\r\nSETNAME\r\n"u8);
        writer.WriteBulkString(name);
    }
}

/// <summary>Validated wire configuration; the default value preserves OPTIN.</summary>
internal readonly record struct ClientTrackingConfiguration
{
    internal RespireClientTrackingMode Mode { get; }
    internal Internal.ClientCachePrefixSet? Prefixes { get; }

    internal ClientTrackingConfiguration(RespireClientTrackingMode mode, IReadOnlyList<RespireKey> prefixes)
        => (Mode, Prefixes) = (mode, Internal.ClientCachePrefixSet.Create(prefixes));
}

/// <summary>CLIENT TRACKING ON with the selected registration mode.</summary>
/// <remarks>
/// Sent after <c>HELLO 3</c> on every cache-bearing connection, including reconnect replacements and
/// discovered Cluster/Sentinel nodes, before the connection is published. OPTIN keeps server tracking
/// memory and pushes limited to deliberate misses; BCAST registers the configured physical prefixes
/// (none means every key). Invalidation pushes arrive on the connection that performed the read, so
/// they stay in wire order with its replies and no redirect connection is needed.
/// </remarks>
internal readonly struct ClientTrackingCommand(ClientTrackingConfiguration configuration = default) : IConnectionProtocolCommand
{
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public void Write(ref RespWriter writer)
    {
        if (configuration.Mode != RespireClientTrackingMode.Broadcast)
        {
            writer.WriteRaw("*4\r\n$6\r\nCLIENT\r\n$8\r\nTRACKING\r\n$2\r\nON\r\n$5\r\nOPTIN\r\n"u8);
            return;
        }
        var prefixes = configuration.Prefixes ?? Internal.ClientCachePrefixSet.Empty;
        writer.WriteArrayHeader(checked(4 + prefixes.Count * 2));
        writer.WriteRaw("$6\r\nCLIENT\r\n$8\r\nTRACKING\r\n$2\r\nON\r\n$5\r\nBCAST\r\n"u8);
        prefixes.WritePrefixes(ref writer);
    }
}

/// <summary>CLIENT CACHING YES.</summary>
/// <remarks>
/// OPTIN prelude for one cacheable read. The prelude and read are written under one write gate with
/// every response slot reserved before any frame is written, so no other producer can interleave a
/// command; a validated multi-reply source drains the prelude replies and returns only the read reply.
/// A Cluster ASK retry writes <c>ASKING</c>, this prelude, and the read as one such sequence. BCAST
/// reads and reads outside the configured prefixes omit the prelude, so Redis does not track them.
/// </remarks>
internal readonly struct ClientCachingCommand : IConnectionProtocolCommand
{
    public int GetWriteSizeHint() => "*3\r\n$6\r\nCLIENT\r\n$7\r\nCACHING\r\n$3\r\nYES\r\n"u8.Length;
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public void Write(ref RespWriter writer)
        => writer.WriteRaw("*3\r\n$6\r\nCLIENT\r\n$7\r\nCACHING\r\n$3\r\nYES\r\n"u8);
}

/// <summary>CLIENT ID.</summary>
internal readonly struct ClientIdCommand : IConnectionProtocolCommand
{
    public int GetWriteSizeHint() => "*2\r\n$6\r\nCLIENT\r\n$2\r\nID\r\n"u8.Length;
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public void Write(ref RespWriter writer)
        => writer.WriteRaw("*2\r\n$6\r\nCLIENT\r\n$2\r\nID\r\n"u8);
}

/// <summary>CLIENT KILL ID id [SKIPME yes].</summary>
internal readonly struct ClientKillIdCommand(long id, bool skipMe = false) : IConnectionProtocolCommand
{
    public ReadCommandKind ReadKind => ReadCommandKind.None;

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
internal readonly struct SelectCommand(int database) : IConnectionProtocolCommand
{
    public int GetWriteSizeHint() => CommandWriteSizeHint.For("*2\r\n$6\r\nSELECT\r\n"u8.Length,
        CommandWriteSizeHint.Bulk(20));
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw("*2\r\n$6\r\nSELECT\r\n"u8);
        writer.WriteBulkInteger(database);
    }
}
