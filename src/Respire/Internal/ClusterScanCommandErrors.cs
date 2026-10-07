namespace Respire.Internal;

internal static class ClusterScanCommandErrors
{
    // Metadata is optional. An ERR response carries no definitive support evidence,
    // regardless of a server fork's wording; retry the probe on a later page.
    internal static bool IsMetadataUnavailable(RespireServerException error)
        => IsDenied(error) || error.Code == "ERR";
    internal static bool IsDenied(RespireServerException error) => error.Code == "NOPERM";

    internal static bool IsUnknown(RespireServerException error, string command)
    {
        const string prefix = "ERR unknown command ";
        if (error.Code != "ERR" || !error.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var name = error.Message.AsSpan(prefix.Length).TrimStart();
        if (name.IsEmpty) return false;
        if (name[0] is '\'' or '"')
        {
            var end = name[1..].IndexOf(name[0]);
            return end >= 0 && name.Slice(1, end).Equals(command, StringComparison.OrdinalIgnoreCase);
        }
        var separator = name.IndexOfAny(' ', ',');
        return (separator < 0 ? name : name[..separator]).Equals(command, StringComparison.OrdinalIgnoreCase);
    }
}
