using System.Collections.Frozen;

namespace Respire.Internal;

internal static class ReadOnlyCommandCatalog
{
    private static readonly FrozenSet<string> s_names = RespireCommands.All.ToArray()
        .Where(static command => command.IsReadOnly)
        .Select(static command => command.Name)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static bool Contains(string operation) => s_names.Contains(operation);
}
