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
/// Use Include and/or Exclude, or the legacy flat selectors. Mixing these forms throws synchronously.
/// Null scalar selectors and empty ID lists are omitted. SkipMe and AllowUnfilteredKill work with either form.
/// </remarks>
public sealed record RespireClientFilterOptions
{
    /// <summary>Positive selectors. Cannot be combined with legacy flat selector properties.</summary>
    public RespireClientIncludeFilters? Include { get; init; }
    /// <summary>Negative selectors. Cannot be combined with legacy flat selector properties.</summary>
    public RespireClientExcludeFilters? Exclude { get; init; }
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
    /// <summary>Exclude this remote address and port. Must be nonempty when specified. Requires Valkey 9+.</summary>
    public string? ExcludedAddress { get; init; }
    /// <summary>Exclude this local address and port. Must be nonempty when specified. Requires Valkey 9+.</summary>
    public string? ExcludedLocalAddress { get; init; }
    /// <summary>Exclude this connection name. Must be nonempty when specified. Requires Valkey 9+.</summary>
    public string? ExcludedName { get; init; }
    /// <summary>Exclude these connection flags. Requires Valkey 9+.</summary>
    public string? ExcludedFlags { get; init; }
    /// <summary>Exclude this library name. Must be nonempty when specified. Requires Valkey 9+.</summary>
    public string? ExcludedLibraryName { get; init; }
    /// <summary>Exclude this library version. Must be nonempty when specified. Requires Valkey 9+.</summary>
    public string? ExcludedLibraryVersion { get; init; }
    /// <summary>Exclude this database number. Requires Valkey 9+.</summary>
    public int? ExcludedDatabase { get; init; }
    /// <summary>Exclude these capabilities. Requires Valkey 9+.</summary>
    public string? ExcludedCapabilities { get; init; }
    /// <summary>Exclude this remote IP address. Must be nonempty when specified. Requires Valkey 9+.</summary>
    public string? ExcludedIp { get; init; }

    internal bool HasFlatSelectors => Type.HasValue || Ids is not { Count: 0 } || User is not null
        || Address is not null || LocalAddress is not null || MaximumAgeSeconds.HasValue || Name is not null
        || IdleSeconds.HasValue || Flags is not null || LibraryName is not null || LibraryVersion is not null
        || Database.HasValue || Capabilities is not null || Ip is not null || ExcludedType.HasValue
        || ExcludedIds is not { Count: 0 } || ExcludedUser is not null || ExcludedAddress is not null
        || ExcludedLocalAddress is not null || ExcludedName is not null || ExcludedFlags is not null
        || ExcludedLibraryName is not null || ExcludedLibraryVersion is not null || ExcludedDatabase.HasValue
        || ExcludedCapabilities is not null || ExcludedIp is not null;
}

internal ref struct ClientFilterArguments(bool kill)
{
    private readonly List<RespireValue> _arguments = [];
    private bool _hasSelector;

    internal static CmdN Build(RespireClientFilterOptions options, bool kill)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((options.Include is not null || options.Exclude is not null) && options.HasFlatSelectors)
            throw new ArgumentException("Include/Exclude cannot be combined with legacy flat selector properties.", nameof(options));
        var writer = new ClientFilterArguments(kill);
        RespireClientIncludeFilters.AppendArguments(options, ref writer);
        RespireClientExcludeFilters.AppendArguments(options, ref writer);
        if (kill && !writer._hasSelector)
        {
            if (!options.AllowUnfilteredKill)
                throw new ArgumentException("CLIENT KILL requires a selector or AllowUnfilteredKill = true.", nameof(options));
            if (!options.SkipMe.HasValue) writer.Add("SKIPME", "yes", isSelector: false);
        }
        return new CmdN(kill ? Verbs.ClientKill : Verbs.ClientList, writer._arguments.ToArray());
    }

    internal void Add(string token, string? value, bool isSelector = true)
    {
        if (value is null) return;
        _arguments.Add(token);
        _arguments.Add(value);
        _hasSelector |= isSelector;
    }

    internal void AddNonEmpty(string token, string? value, string propertyName)
    {
        if (value is "")
            throw new ArgumentException($"{propertyName} must be nonempty when specified.", "options");
        Add(token, value);
    }

    internal void AddNumber(string token, long? value, string propertyName, bool positive = false)
    {
        if (value is not { } number) return;
        if (number < 0 || (positive && number == 0))
            throw new ArgumentOutOfRangeException("options", number,
                $"{propertyName} must be {(positive ? "positive" : "nonnegative")}.");
        _arguments.Add(token);
        _arguments.Add(number);
        _hasSelector = true;
    }

    internal void AddType(string token, RespireClientType? type, string propertyName)
    {
        if (type is null) return;
        Add(token, type switch
        {
            RespireClientType.Normal => "normal",
            RespireClientType.Primary => "master",
            RespireClientType.Replica => kill ? "slave" : "replica",
            RespireClientType.PubSub => "pubsub",
            _ => throw new ArgumentOutOfRangeException("options", type, $"{propertyName} is an unknown client type."),
        });
    }

    internal void AddIds(string token, IReadOnlyList<long> ids, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return;
        _arguments.Add(token);
        foreach (var id in ids)
        {
            if (id <= 0)
                throw new ArgumentOutOfRangeException("options", id, $"{propertyName} must contain only positive client IDs.");
            _arguments.Add(id);
        }
        _hasSelector = true;
    }
}
