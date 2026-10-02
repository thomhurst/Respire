using Respire.Protocol;

namespace Respire.Internal;

/// <summary>How a command may be routed by a read policy.</summary>
internal static class CursorCommandMetadata
{
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
