using System.Collections.Frozen;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>How a command may be routed by a read policy.</summary>
internal enum ReadCommandKind : byte
{
    /// <summary>Not catalogued as read-only; always sent to the primary.</summary>
    None,
    /// <summary>Catalogued as read-only; any eligible endpoint may serve it.</summary>
    Read,
    /// <summary>Read-only and cursor-based; successive pages must reach the server that issued the cursor.</summary>
    CursorRead,
}

internal static class ReadOnlyCommandCatalog
{
    // Cursors returned by these commands are local to the server that issued them.
    private static readonly FrozenSet<string> s_cursorNames = new[] { "SCAN", "HSCAN", "SSCAN", "ZSCAN", "ARSCAN" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, ReadCommandKind> s_kinds = RespireCommands.All.ToArray()
        .Where(static command => command.IsReadOnly)
        .Select(static command => command.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToFrozenDictionary(
            static name => name,
            static name => s_cursorNames.Contains(name) ? ReadCommandKind.CursorRead : ReadCommandKind.Read,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Classifies an operation with one lookup.</summary>
    internal static ReadCommandKind Classify(string operation)
        => s_kinds.GetValueOrDefault(operation, ReadCommandKind.None);

    internal static bool Contains(string operation) => s_kinds.ContainsKey(operation);

    /// <summary>
    /// True when a cursor command continues an earlier scan (its cursor is not <c>0</c>), so it must
    /// reach the server that issued the cursor. Commands whose cursor position is unknown, such as
    /// <c>ARSCAN</c>, are treated as starting a scan.
    /// </summary>
    internal static bool IsCursorContinuation<TCommand>(string operation, in TCommand command)
        where TCommand : struct, IRespCommand
    {
        var index = CursorArgumentIndex(operation);
        if (index < 0 || !command.TryGetArgument(index, out var cursor)) return false;
        return !(cursor.TryGetInt64(out var value) && value == 0);
    }

    // SCAN cursor ...; HSCAN/SSCAN/ZSCAN key cursor ...
    private static int CursorArgumentIndex(string operation)
        => operation.Equals("SCAN", StringComparison.OrdinalIgnoreCase) ? 0
            : operation.Equals("HSCAN", StringComparison.OrdinalIgnoreCase)
                || operation.Equals("SSCAN", StringComparison.OrdinalIgnoreCase)
                || operation.Equals("ZSCAN", StringComparison.OrdinalIgnoreCase) ? 1
            : -1;
}
