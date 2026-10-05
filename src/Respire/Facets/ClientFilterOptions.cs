using Respire.Commands;

namespace Respire;

/// <summary>A connection class used by CLIENT LIST and CLIENT KILL.</summary>
public enum RespireClientType
{
    /// <summary>Ordinary connections, including MONITOR clients.</summary>
    Normal,
    /// <summary>The upstream primary connection (encoded as MASTER for compatibility).</summary>
    Primary,
    /// <summary>Downstream replica connections.</summary>
    Replica,
    /// <summary>Pub/sub connections.</summary>
    PubSub,
}

/// <summary>Filters combined with logical AND by CLIENT LIST or CLIENT KILL.</summary>
/// <remarks>
/// LIST supports Type on Redis 5+ and Ids on Redis 6.2+, but Redis through 8.10 rejects combining them.
/// LIST combinations and its other common filters require Valkey 8.1+.
/// KILL supports common filters on Redis; LocalAddress requires Redis 6.2+, MaximumAgeSeconds Redis 7.4+,
/// and multiple Ids Valkey 8.1+. Name, IdleSeconds, Flags, LibraryName, LibraryVersion, Database,
/// Capabilities, Ip and all Excluded filters require Valkey 9+. Unsupported filters produce server errors.
/// IDs belong to one server. Values are copied when the operation is called or queued; later collection changes have no effect.
/// </remarks>
public sealed record RespireClientFilterOptions
{
    /// <summary>Include only this connection class.</summary>
    public RespireClientType? Type { get; init; }
    /// <summary>Include any of these positive client IDs; empty means no ID filter.</summary>
    public IReadOnlyList<long> Ids { get; init; } = [];
    /// <summary>Match an ACL username.</summary>
    public string? User { get; init; }
    /// <summary>Match the remote address and port as reported by CLIENT LIST.</summary>
    public string? Address { get; init; }
    /// <summary>Match the local address and port as reported by CLIENT LIST.</summary>
    public string? LocalAddress { get; init; }
    /// <summary>Exclude the executing connection. Null preserves the server default (yes for KILL, no for LIST).</summary>
    public bool? SkipMe { get; init; }
    /// <summary>Match connections at least this old, in whole seconds (MAXAGE).</summary>
    public long? MaximumAgeSeconds { get; init; }
    /// <summary>Match the connection name. Requires Valkey 9+.</summary>
    public string? Name { get; init; }
    /// <summary>Match connections idle for at least this many seconds. Requires Valkey 9+.</summary>
    public long? IdleSeconds { get; init; }
    /// <summary>Match connection flags. Requires Valkey 9+.</summary>
    public string? Flags { get; init; }
    /// <summary>Match the library name. Requires Valkey 9+.</summary>
    public string? LibraryName { get; init; }
    /// <summary>Match the library version. Requires Valkey 9+.</summary>
    public string? LibraryVersion { get; init; }
    /// <summary>Match the database number. Requires Valkey 9+.</summary>
    public int? Database { get; init; }
    /// <summary>Match connection capabilities. Requires Valkey 9+.</summary>
    public string? Capabilities { get; init; }
    /// <summary>Match the remote IP address. Requires Valkey 9+.</summary>
    public string? Ip { get; init; }
    /// <summary>Exclude this connection class. Requires Valkey 9+.</summary>
    public RespireClientType? ExcludedType { get; init; }
    /// <summary>Exclude these positive IDs. Requires Valkey 9+.</summary>
    public IReadOnlyList<long> ExcludedIds { get; init; } = [];
    /// <summary>Exclude this ACL username. Requires Valkey 9+.</summary>
    public string? ExcludedUser { get; init; }
    /// <summary>Exclude this remote address and port. Requires Valkey 9+.</summary>
    public string? ExcludedAddress { get; init; }
    /// <summary>Exclude this local address and port. Requires Valkey 9+.</summary>
    public string? ExcludedLocalAddress { get; init; }
    /// <summary>Exclude this connection name. Requires Valkey 9+.</summary>
    public string? ExcludedName { get; init; }
    /// <summary>Exclude these connection flags. Requires Valkey 9+.</summary>
    public string? ExcludedFlags { get; init; }
    /// <summary>Exclude this library name. Requires Valkey 9+.</summary>
    public string? ExcludedLibraryName { get; init; }
    /// <summary>Exclude this library version. Requires Valkey 9+.</summary>
    public string? ExcludedLibraryVersion { get; init; }
    /// <summary>Exclude this database number. Requires Valkey 9+.</summary>
    public int? ExcludedDatabase { get; init; }
    /// <summary>Exclude these capabilities. Requires Valkey 9+.</summary>
    public string? ExcludedCapabilities { get; init; }
    /// <summary>Exclude this remote IP address. Requires Valkey 9+.</summary>
    public string? ExcludedIp { get; init; }
}

internal static class ClientFilterArguments
{
    internal static CmdN Build(RespireClientFilterOptions options, bool kill)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<RespireValue>();
        AddType("TYPE", options.Type);
        AddIds("ID", options.Ids);
        Add("USER", options.User);
        Add("ADDR", options.Address);
        Add("LADDR", options.LocalAddress);
        if (options.SkipMe is { } skip) Add("SKIPME", skip ? "yes" : "no");
        AddNumber("MAXAGE", options.MaximumAgeSeconds, positive: true);
        Add("NAME", options.Name);
        AddNumber("IDLE", options.IdleSeconds, positive: true);
        Add("FLAGS", options.Flags);
        Add("LIB-NAME", options.LibraryName);
        Add("LIB-VER", options.LibraryVersion);
        AddNumber("DB", options.Database);
        Add("CAPA", options.Capabilities);
        Add("IP", options.Ip);
        AddType("NOT-TYPE", options.ExcludedType);
        AddIds("NOT-ID", options.ExcludedIds);
        Add("NOT-USER", options.ExcludedUser);
        Add("NOT-ADDR", options.ExcludedAddress);
        Add("NOT-LADDR", options.ExcludedLocalAddress);
        Add("NOT-NAME", options.ExcludedName);
        Add("NOT-FLAGS", options.ExcludedFlags);
        Add("NOT-LIB-NAME", options.ExcludedLibraryName);
        Add("NOT-LIB-VER", options.ExcludedLibraryVersion);
        AddNumber("NOT-DB", options.ExcludedDatabase);
        Add("NOT-CAPA", options.ExcludedCapabilities);
        Add("NOT-IP", options.ExcludedIp);
        if (kill && args.Count == 0)
            throw new ArgumentException("CLIENT KILL requires at least one explicit filter.", nameof(options));
        return new CmdN(kill ? Verbs.ClientKill : Verbs.ClientList, args.ToArray());

        void Add(string token, string? value)
        {
            if (value is null) return;
            args.Add(token);
            args.Add(value);
        }

        void AddNumber(string token, long? value, bool positive = false)
        {
            if (value is not { } number) return;
            ArgumentOutOfRangeException.ThrowIfNegative(number, nameof(options));
            if (positive) ArgumentOutOfRangeException.ThrowIfZero(number, nameof(options));
            args.Add(token);
            args.Add(number);
        }

        void AddType(string token, RespireClientType? type)
        {
            if (type is null) return;
            Add(token, type switch
            {
                RespireClientType.Normal => "normal",
                RespireClientType.Primary => "master",
                RespireClientType.Replica => "replica",
                RespireClientType.PubSub => "pubsub",
                _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown client type."),
            });
        }

        void AddIds(string token, IReadOnlyList<long> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) return;
            args.Add(token);
            foreach (var id in ids)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id, nameof(options));
                args.Add(id);
            }
        }
    }
}
