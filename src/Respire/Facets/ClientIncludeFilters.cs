namespace Respire;

/// <summary>Positive CLIENT LIST/KILL selectors combined with logical AND.</summary>
/// <remarks>
/// IDs within one list match any listed client. Server/version restrictions are the same as
/// <see cref="RespireClientFilterOptions"/>. Values are validated and IDs copied when called or queued.
/// </remarks>
public sealed record RespireClientIncludeFilters
{
    /// <summary>Include this connection class.</summary>
    public RespireClientType? Type { get; init; }
    /// <summary>Include any of these positive IDs; an empty list omits this selector.</summary>
    public IReadOnlyList<long> Ids { get; init; } = [];
    /// <summary>Match the ACL username.</summary>
    public string? User { get; init; }
    /// <summary>Match the remote address and port.</summary>
    public string? Address { get; init; }
    /// <summary>Match the local address and port.</summary>
    public string? LocalAddress { get; init; }
    /// <summary>Match connections at least this old, in positive whole seconds (MAXAGE).</summary>
    public long? MaximumAgeSeconds { get; init; }
    /// <summary>Match the connection name. Requires Valkey 9+.</summary>
    public string? Name { get; init; }
    /// <summary>Match connections idle for this many positive whole seconds. Requires Valkey 9+.</summary>
    public long? IdleSeconds { get; init; }
    /// <summary>Match a nonempty set of flags. Requires Valkey 9+.</summary>
    public string? Flags { get; init; }
    /// <summary>Match the library name. Requires Valkey 9+.</summary>
    public string? LibraryName { get; init; }
    /// <summary>Match the library version. Requires Valkey 9+.</summary>
    public string? LibraryVersion { get; init; }
    /// <summary>Match a nonnegative database number. Requires Valkey 9+.</summary>
    public int? Database { get; init; }
    /// <summary>Match a nonempty set of capabilities. Requires Valkey 9+.</summary>
    public string? Capabilities { get; init; }
    /// <summary>Match the remote IP address. Requires Valkey 9+.</summary>
    public string? Ip { get; init; }

    internal static void AppendArguments(RespireClientFilterOptions options, ref ClientFilterArguments writer)
    {
        var selectors = ClientFilterSelectors.ForInclude(options);
        writer.PropertyPrefix = selectors.PropertyPrefix;
        writer.AddType("TYPE", selectors.Type, nameof(selectors.Type));
        writer.AddIds("ID", selectors.Ids, nameof(selectors.Ids));
        writer.Add("USER", selectors.User);
        writer.Add("ADDR", selectors.Address);
        writer.Add("LADDR", selectors.LocalAddress);
        if (options.SkipMe is { } skip) writer.Add("SKIPME", skip ? "yes" : "no", isSelector: false);
        writer.AddNumber("MAXAGE", selectors.MaximumAgeSeconds, nameof(selectors.MaximumAgeSeconds), positive: true);
        writer.Add("NAME", selectors.Name);
        writer.AddNumber("IDLE", selectors.IdleSeconds, nameof(selectors.IdleSeconds), positive: true);
        writer.AddNonEmpty("FLAGS", selectors.Flags, nameof(selectors.Flags));
        writer.Add("LIB-NAME", selectors.LibraryName);
        writer.Add("LIB-VER", selectors.LibraryVersion);
        writer.AddNumber("DB", selectors.Database, nameof(selectors.Database));
        writer.AddNonEmpty("CAPA", selectors.Capabilities, nameof(selectors.Capabilities));
        writer.Add("IP", selectors.Ip);
    }
}
