using System.Collections.Frozen;

namespace Respire.Internal;

/// <summary>
/// Read-only classification for the operation names that typed facets pass to the send path.
/// </summary>
/// <remarks>
/// Names use the catalog's canonical form, including subcommands (for example,
/// <c>OBJECT ENCODING</c>). Raw execution never consults this set by name alone: a
/// <see cref="RespireCommand"/> must itself carry <see cref="RespireCommand.IsReadOnly"/>, which
/// caller-supplied descriptors never do. Lookups happen only when a non-primary read policy is
/// active, and batches resolve the policy once per slot group.
/// </remarks>
internal static class ReadOnlyCommandMetadata
{
    private static readonly FrozenSet<string> s_readOnlyCommands = Build();

    internal static bool IsReadOnly(string operation) => s_readOnlyCommands.Contains(operation);

    private static FrozenSet<string> Build()
    {
        var names = new List<string>();
        foreach (var command in RespireCommands.All)
        {
            if (command.IsReadOnly) names.Add(command.Name);
        }

        return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
