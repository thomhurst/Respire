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
    /// <summary>Explicitly permits KILL without a selector, including a SkipMe-only call. Ignored by LIST.</summary>
    public bool AllowUnfilteredKill { get; init; }
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
    /// <summary>Match connections at least this old, in whole seconds (MAXAGE), for both LIST and KILL.</summary>
    public long? MaximumAgeSeconds { get; init; }
    /// <summary>Match the connection name. Requires Valkey 9+.</summary>
    public string? Name { get; init; }
    /// <summary>Match connections idle for at least this many seconds. Must be positive. Requires Valkey 9+.</summary>
    public long? IdleSeconds { get; init; }
    /// <summary>Match connection flags. Must be nonempty when specified. Requires Valkey 9+.</summary>
    public string? Flags { get; init; }
    /// <summary>Match the library name. Requires Valkey 9+.</summary>
    public string? LibraryName { get; init; }
    /// <summary>Match the library version. Requires Valkey 9+.</summary>
    public string? LibraryVersion { get; init; }
    /// <summary>Match the database number. Requires Valkey 9+.</summary>
    public int? Database { get; init; }
    /// <summary>Match connection capabilities. Must be nonempty when specified. Requires Valkey 9+.</summary>
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
        if (options.Flags is "" || options.Capabilities is "")
            throw new ArgumentException("Flags and Capabilities must be nonempty when specified.", nameof(options));
        var args = new List<RespireValue>();
        var hasSelector = false;
        AddType("TYPE", options.Type, nameof(options.Type));
        AddIds("ID", options.Ids, nameof(options.Ids));
        Add("USER", options.User);
        Add("ADDR", options.Address);
        Add("LADDR", options.LocalAddress);
        if (options.SkipMe is { } skip) Add("SKIPME", skip ? "yes" : "no", isSelector: false);
        AddNumber("MAXAGE", options.MaximumAgeSeconds, nameof(options.MaximumAgeSeconds), positive: true);
        Add("NAME", options.Name);
        AddNumber("IDLE", options.IdleSeconds, nameof(options.IdleSeconds), positive: true);
        Add("FLAGS", options.Flags);
        Add("LIB-NAME", options.LibraryName);
        Add("LIB-VER", options.LibraryVersion);
        AddNumber("DB", options.Database, nameof(options.Database));
        Add("CAPA", options.Capabilities);
        Add("IP", options.Ip);
        AddType("NOT-TYPE", options.ExcludedType, nameof(options.ExcludedType));
        AddIds("NOT-ID", options.ExcludedIds, nameof(options.ExcludedIds));
        Add("NOT-USER", options.ExcludedUser);
        Add("NOT-ADDR", options.ExcludedAddress);
        Add("NOT-LADDR", options.ExcludedLocalAddress);
        Add("NOT-NAME", options.ExcludedName);
        Add("NOT-FLAGS", options.ExcludedFlags);
        Add("NOT-LIB-NAME", options.ExcludedLibraryName);
        Add("NOT-LIB-VER", options.ExcludedLibraryVersion);
        AddNumber("NOT-DB", options.ExcludedDatabase, nameof(options.ExcludedDatabase));
        Add("NOT-CAPA", options.ExcludedCapabilities);
        Add("NOT-IP", options.ExcludedIp);
        if (kill && !hasSelector)
        {
            if (!options.AllowUnfilteredKill)
                throw new ArgumentException("CLIENT KILL requires a selector or AllowUnfilteredKill = true.", nameof(options));
            if (!options.SkipMe.HasValue) Add("SKIPME", "yes", isSelector: false);
        }
        return new CmdN(kill ? Verbs.ClientKill : Verbs.ClientList, args.ToArray());

        void Add(string token, string? value, bool isSelector = true)
        {
            if (value is null) return;
            args.Add(token);
            args.Add(value);
            hasSelector |= isSelector;
        }

        void AddNumber(string token, long? value, string propertyName, bool positive = false)
        {
            if (value is not { } number) return;
            if (number < 0 || (positive && number == 0))
                throw new ArgumentOutOfRangeException(nameof(options), number,
                    $"{propertyName} must be {(positive ? "positive" : "nonnegative")}.");
            args.Add(token);
            args.Add(number);
            hasSelector = true;
        }

        void AddType(string token, RespireClientType? type, string propertyName)
        {
            if (type is null) return;
            Add(token, type switch
            {
                RespireClientType.Normal => "normal",
                RespireClientType.Primary => "master",
                RespireClientType.Replica => kill ? "slave" : "replica",
                RespireClientType.PubSub => "pubsub",
                _ => throw new ArgumentOutOfRangeException(nameof(options), type, $"{propertyName} is an unknown client type."),
            });
        }

        void AddIds(string token, IReadOnlyList<long> ids, string propertyName)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) return;
            args.Add(token);
            foreach (var id in ids)
            {
                if (id <= 0)
                    throw new ArgumentOutOfRangeException(nameof(options), id, $"{propertyName} must contain only positive client IDs.");
                args.Add(id);
            }
            hasSelector = true;
        }
    }
}
