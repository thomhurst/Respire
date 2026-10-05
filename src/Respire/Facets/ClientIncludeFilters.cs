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
        var group = options.Include;
        writer.AddType("TYPE", group is null ? options.Type : group.Type, group is null ? "Type" : "Include.Type");
        writer.AddIds("ID", group is null ? options.Ids : group.Ids, group is null ? "Ids" : "Include.Ids");
        writer.Add("USER", group is null ? options.User : group.User);
        writer.Add("ADDR", group is null ? options.Address : group.Address);
        writer.Add("LADDR", group is null ? options.LocalAddress : group.LocalAddress);
        if (options.SkipMe is { } skip) writer.Add("SKIPME", skip ? "yes" : "no", isSelector: false);
        writer.AddNumber("MAXAGE", group is null ? options.MaximumAgeSeconds : group.MaximumAgeSeconds,
            group is null ? "MaximumAgeSeconds" : "Include.MaximumAgeSeconds", positive: true);
        writer.Add("NAME", group is null ? options.Name : group.Name);
        writer.AddNumber("IDLE", group is null ? options.IdleSeconds : group.IdleSeconds,
            group is null ? "IdleSeconds" : "Include.IdleSeconds", positive: true);
        writer.AddNonEmpty("FLAGS", group is null ? options.Flags : group.Flags, group is null ? "Flags" : "Include.Flags");
        writer.Add("LIB-NAME", group is null ? options.LibraryName : group.LibraryName);
        writer.Add("LIB-VER", group is null ? options.LibraryVersion : group.LibraryVersion);
        writer.AddNumber("DB", group is null ? options.Database : group.Database, group is null ? "Database" : "Include.Database");
        writer.AddNonEmpty("CAPA", group is null ? options.Capabilities : group.Capabilities,
            group is null ? "Capabilities" : "Include.Capabilities");
        writer.Add("IP", group is null ? options.Ip : group.Ip);
    }
}
