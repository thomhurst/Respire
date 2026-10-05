namespace Respire;

/// <summary>Negative CLIENT LIST/KILL selectors. Requires Valkey 9+.</summary>
/// <remarks>
/// Exclusion-only KILL can close almost every connection. Prefer an Include selector for owned IDs.
/// Empty Flags and Capabilities exclude every connection; empty address/metadata exclusions are invalid.
/// Values are validated and IDs copied when called or queued.
/// </remarks>
public sealed record RespireClientExcludeFilters
{
    /// <summary>Exclude this connection class.</summary>
    public RespireClientType? Type { get; init; }
    /// <summary>Exclude these positive IDs; an empty list omits this selector.</summary>
    public IReadOnlyList<long> Ids { get; init; } = [];
    /// <summary>Exclude this ACL username.</summary>
    public string? User { get; init; }
    /// <summary>Exclude this nonempty remote address and port.</summary>
    public string? Address { get; init; }
    /// <summary>Exclude this nonempty local address and port.</summary>
    public string? LocalAddress { get; init; }
    /// <summary>Exclude this nonempty connection name.</summary>
    public string? Name { get; init; }
    /// <summary>Exclude these flags; empty excludes every connection.</summary>
    public string? Flags { get; init; }
    /// <summary>Exclude this nonempty library name.</summary>
    public string? LibraryName { get; init; }
    /// <summary>Exclude this nonempty library version.</summary>
    public string? LibraryVersion { get; init; }
    /// <summary>Exclude this nonnegative database number.</summary>
    public int? Database { get; init; }
    /// <summary>Exclude these capabilities; empty excludes every connection.</summary>
    public string? Capabilities { get; init; }
    /// <summary>Exclude this nonempty remote IP address.</summary>
    public string? Ip { get; init; }

    internal static void AppendArguments(RespireClientFilterOptions options, ref ClientFilterArguments writer)
    {
        var group = options.Exclude;
        writer.AddType("NOT-TYPE", group is null ? options.ExcludedType : group.Type,
            group is null ? "ExcludedType" : "Exclude.Type");
        writer.AddIds("NOT-ID", group is null ? options.ExcludedIds : group.Ids,
            group is null ? "ExcludedIds" : "Exclude.Ids");
        writer.Add("NOT-USER", group is null ? options.ExcludedUser : group.User);
        // Empty address/metadata exclusions can exclude nobody; empty flag/capability sets exclude everybody.
        writer.AddNonEmpty("NOT-ADDR", group is null ? options.ExcludedAddress : group.Address,
            group is null ? "ExcludedAddress" : "Exclude.Address");
        writer.AddNonEmpty("NOT-LADDR", group is null ? options.ExcludedLocalAddress : group.LocalAddress,
            group is null ? "ExcludedLocalAddress" : "Exclude.LocalAddress");
        writer.AddNonEmpty("NOT-NAME", group is null ? options.ExcludedName : group.Name,
            group is null ? "ExcludedName" : "Exclude.Name");
        writer.Add("NOT-FLAGS", group is null ? options.ExcludedFlags : group.Flags);
        writer.AddNonEmpty("NOT-LIB-NAME", group is null ? options.ExcludedLibraryName : group.LibraryName,
            group is null ? "ExcludedLibraryName" : "Exclude.LibraryName");
        writer.AddNonEmpty("NOT-LIB-VER", group is null ? options.ExcludedLibraryVersion : group.LibraryVersion,
            group is null ? "ExcludedLibraryVersion" : "Exclude.LibraryVersion");
        writer.AddNumber("NOT-DB", group is null ? options.ExcludedDatabase : group.Database,
            group is null ? "ExcludedDatabase" : "Exclude.Database");
        writer.Add("NOT-CAPA", group is null ? options.ExcludedCapabilities : group.Capabilities);
        writer.AddNonEmpty("NOT-IP", group is null ? options.ExcludedIp : group.Ip,
            group is null ? "ExcludedIp" : "Exclude.Ip");
    }
}
