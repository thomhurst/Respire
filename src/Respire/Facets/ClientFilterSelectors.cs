namespace Respire;

/// <summary>Selects flat or grouped values without allocating a group; names are formatted only on validation failure.</summary>
internal readonly record struct ClientFilterSelectors(
    RespireClientType? Type, IReadOnlyList<long> Ids, string? User, string? Address,
    string? LocalAddress, string? Name, string? Flags, string? LibraryName, string? LibraryVersion,
    int? Database, string? Capabilities, string? Ip, string PropertyPrefix,
    long? MaximumAgeSeconds = null, long? IdleSeconds = null)
{
    internal static ClientFilterSelectors ForInclude(RespireClientFilterOptions options)
        => options.Include is { } group
            ? new(group.Type, group.Ids, group.User, group.Address, group.LocalAddress, group.Name,
                group.Flags, group.LibraryName, group.LibraryVersion, group.Database, group.Capabilities,
                group.Ip, "Include.", group.MaximumAgeSeconds, group.IdleSeconds)
            : new(options.Type, options.Ids, options.User, options.Address, options.LocalAddress, options.Name,
                options.Flags, options.LibraryName, options.LibraryVersion, options.Database, options.Capabilities,
                options.Ip, "", options.MaximumAgeSeconds, options.IdleSeconds);

    internal static ClientFilterSelectors ForExclude(RespireClientFilterOptions options)
        => options.Exclude is { } group
            ? new(group.Type, group.Ids, group.User, group.Address, group.LocalAddress, group.Name,
                group.Flags, group.LibraryName, group.LibraryVersion, group.Database, group.Capabilities,
                group.Ip, "Exclude.")
            : new(options.ExcludedType, options.ExcludedIds, options.ExcludedUser, options.ExcludedAddress,
                options.ExcludedLocalAddress, options.ExcludedName, options.ExcludedFlags, options.ExcludedLibraryName,
                options.ExcludedLibraryVersion, options.ExcludedDatabase, options.ExcludedCapabilities,
                options.ExcludedIp, "Excluded");
}
