namespace Respire.Internal;

/// <summary>Immutable Sentinel wire capabilities, computed once from INFO SERVER.</summary>
internal readonly record struct SentinelServerProfile(
    string PrimaryCommand,
    string PrimariesCommand,
    string ReplicasCommand,
    string DownStateCommand,
    bool SupportsMultiOptionConfig)
{
    internal static SentinelServerProfile Legacy => new("MASTER", "MASTERS", "SLAVES", "IS-MASTER-DOWN-BY-ADDR", false);

    internal static SentinelServerProfile FromInfo(string info)
    {
        var valkey = ServerVersion(info, "valkey_version:");
        var redis = ServerVersion(info, "redis_version:");
        var primaryAliases = valkey is { Major: >= 8 };
        return new(
            primaryAliases ? "PRIMARY" : "MASTER",
            primaryAliases ? "PRIMARIES" : "MASTERS",
            valkey is not null || redis is { Major: >= 5 } ? "REPLICAS" : "SLAVES",
            primaryAliases ? "IS-PRIMARY-DOWN-BY-ADDR" : "IS-MASTER-DOWN-BY-ADDR",
            valkey is { Major: >= 8 } || redis is { Major: > 7 } or { Major: 7, Minor: >= 2 });
    }

    private static Version? ServerVersion(string info, string prefix)
    {
        foreach (var line in info.Split('\n'))
            if (line.StartsWith(prefix, StringComparison.Ordinal)
                && Version.TryParse(line[prefix.Length..].Trim(), out var version)) return version;
        return null;
    }
}
