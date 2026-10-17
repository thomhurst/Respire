using Respire.Protocol;

namespace Respire.Commands;

/// <summary>A fully pre-encoded command frame (PING, FLUSHDB, ...).</summary>
internal readonly struct RawCommand(byte[] preEncoded, ReadCommandKind readKind = ReadCommandKind.None) : IRespCommand
{
    // Only these library-owned constant frames carry audited permission. Equal caller bytes
    // and a familiar diagnostic name do not authorize replay of an arbitrary raw request.
    public RespireCommandRetryCategory RetryCategory
    {
        get
        {
            if (ReferenceEquals(preEncoded, RespCommands.Ping)) return CommandRetryCategoryMetadata.Get("PING");
            if (ReferenceEquals(preEncoded, RespCommands.DbSize)) return CommandRetryCategoryMetadata.Get("DBSIZE");
            if (ReferenceEquals(preEncoded, RespCommands.Info)) return CommandRetryCategoryMetadata.Get("INFO");
            if (ReferenceEquals(preEncoded, RespCommands.Time)) return CommandRetryCategoryMetadata.Get("TIME");
            if (ReferenceEquals(preEncoded, RespCommands.LastSave)) return CommandRetryCategoryMetadata.Get("LASTSAVE");
            if (ReferenceEquals(preEncoded, RespCommands.Role)) return CommandRetryCategoryMetadata.Get("ROLE");
            if (ReferenceEquals(preEncoded, RespCommands.RandomKey)) return CommandRetryCategoryMetadata.Get("RANDOMKEY");
            if (ReferenceEquals(preEncoded, RespCommands.FlushDb)) return CommandRetryCategoryMetadata.Get("FLUSHDB");
            if (ReferenceEquals(preEncoded, RespCommands.FlushAll)) return CommandRetryCategoryMetadata.Get("FLUSHALL");
            if (ReferenceEquals(preEncoded, RespCommands.Save)) return CommandRetryCategoryMetadata.Get("SAVE");
            if (ReferenceEquals(preEncoded, RespCommands.BgSave)) return CommandRetryCategoryMetadata.Get("BGSAVE");
            return RespireCommandRetryCategory.Never;
        }
    }
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
    internal bool NoLoop { get; }

    internal ClientTrackingConfiguration(RespireClientTrackingMode mode, IReadOnlyList<RespireKey> prefixes, bool noLoop = false)
        => (Mode, Prefixes, NoLoop) = (mode, Internal.ClientCachePrefixSet.Create(prefixes), noLoop);
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
        var prefixes = configuration.Prefixes ?? Internal.ClientCachePrefixSet.Empty;
        var broadcast = configuration.Mode == RespireClientTrackingMode.Broadcast;
        writer.WriteArrayHeader(checked(4 + (broadcast ? prefixes.Count * 2 : 0) + (configuration.NoLoop ? 1 : 0)));
        writer.WriteRaw("$6\r\nCLIENT\r\n$8\r\nTRACKING\r\n$2\r\nON\r\n"u8);
        if (broadcast) writer.WriteRaw("$5\r\nBCAST\r\n"u8);
        else if (configuration.Mode == RespireClientTrackingMode.OptOut) writer.WriteRaw("$6\r\nOPTOUT\r\n"u8);
        else writer.WriteRaw("$5\r\nOPTIN\r\n"u8);
        if (broadcast) prefixes.WritePrefixes(ref writer);
        if (configuration.NoLoop) writer.WriteRaw("$6\r\nNOLOOP\r\n"u8);
    }
}

/// <summary>CLIENT CACHING YES (OPTIN) or NO (OPTOUT).</summary>
/// <remarks>
/// One-shot prelude for a cacheable OPTIN read or a bypassed OPTOUT read. The prelude and read are written under one write gate with
/// every response slot reserved before any frame is written, so no other producer can interleave a
/// command; a validated multi-reply source drains the prelude replies and returns only the read reply.
/// A Cluster ASK retry writes <c>ASKING</c>, this prelude, and the read as one such sequence. BCAST
/// reads omit the prelude. OPTIN reads outside the configured prefixes also omit it and stay untracked.
/// </remarks>
internal readonly struct ClientCachingCommand(bool optOut = false) : IConnectionProtocolCommand
{
    public int GetWriteSizeHint() => Frame.Length;
    public ReadCommandKind ReadKind => ReadCommandKind.None;
    private ReadOnlySpan<byte> Frame => optOut
        ? "*3\r\n$6\r\nCLIENT\r\n$7\r\nCACHING\r\n$2\r\nNO\r\n"u8
        : "*3\r\n$6\r\nCLIENT\r\n$7\r\nCACHING\r\n$3\r\nYES\r\n"u8;

    public void Write(ref RespWriter writer)
        => writer.WriteRaw(Frame);
}

/// <summary>CLIENT ID.</summary>
internal readonly struct ClientIdCommand : IConnectionProtocolCommand
{
    public int GetWriteSizeHint() => "*2\r\n$6\r\nCLIENT\r\n$2\r\nID\r\n"u8.Length;
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public void Write(ref RespWriter writer)
        => writer.WriteRaw("*2\r\n$6\r\nCLIENT\r\n$2\r\nID\r\n"u8);
}

/// <summary>CLIENT UNBLOCK id TIMEOUT.</summary>
internal readonly struct ClientUnblockCommand(long id) : IConnectionProtocolCommand
{
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public void Write(ref RespWriter writer)
    {
        writer.WriteRaw("*4\r\n$6\r\nCLIENT\r\n$7\r\nUNBLOCK\r\n"u8);
        writer.WriteBulkInteger(id);
        writer.WriteRaw("$7\r\nTIMEOUT\r\n"u8);
    }
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
