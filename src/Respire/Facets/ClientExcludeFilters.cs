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
        var selectors = ClientFilterSelectors.ForExclude(options);
        writer.PropertyPrefix = selectors.PropertyPrefix;
        writer.AddType("NOT-TYPE", selectors.Type, nameof(selectors.Type));
        writer.AddIds("NOT-ID", selectors.Ids, nameof(selectors.Ids));
        writer.Add("NOT-USER", selectors.User);
        // Empty address/metadata exclusions can exclude nobody; empty flag/capability sets exclude everybody.
        writer.AddNonEmpty("NOT-ADDR", selectors.Address, nameof(selectors.Address));
        writer.AddNonEmpty("NOT-LADDR", selectors.LocalAddress, nameof(selectors.LocalAddress));
        writer.AddNonEmpty("NOT-NAME", selectors.Name, nameof(selectors.Name));
        writer.Add("NOT-FLAGS", selectors.Flags);
        writer.AddNonEmpty("NOT-LIB-NAME", selectors.LibraryName, nameof(selectors.LibraryName));
        writer.AddNonEmpty("NOT-LIB-VER", selectors.LibraryVersion, nameof(selectors.LibraryVersion));
        writer.AddNumber("NOT-DB", selectors.Database, nameof(selectors.Database));
        writer.Add("NOT-CAPA", selectors.Capabilities);
        writer.AddNonEmpty("NOT-IP", selectors.Ip, nameof(selectors.Ip));
    }
}
