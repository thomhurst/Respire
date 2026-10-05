namespace Respire;

internal static class ClientFilterTestOptions
{
    internal static RespireClientFilterOptions Group(RespireClientFilterOptions options) => new()
    {
        AllowUnfilteredKill = options.AllowUnfilteredKill,
        SkipMe = options.SkipMe,
        Include = new()
        {
            Type = options.Type, Ids = options.Ids, User = options.User,
            Address = options.Address, LocalAddress = options.LocalAddress,
            MaximumAgeSeconds = options.MaximumAgeSeconds, Name = options.Name,
            IdleSeconds = options.IdleSeconds, Flags = options.Flags,
            LibraryName = options.LibraryName, LibraryVersion = options.LibraryVersion,
            Database = options.Database, Capabilities = options.Capabilities, Ip = options.Ip,
        },
        Exclude = new()
        {
            Type = options.ExcludedType, Ids = options.ExcludedIds, User = options.ExcludedUser,
            Address = options.ExcludedAddress, LocalAddress = options.ExcludedLocalAddress,
            Name = options.ExcludedName, Flags = options.ExcludedFlags,
            LibraryName = options.ExcludedLibraryName, LibraryVersion = options.ExcludedLibraryVersion,
            Database = options.ExcludedDatabase, Capabilities = options.ExcludedCapabilities,
            Ip = options.ExcludedIp,
        },
    };
}
