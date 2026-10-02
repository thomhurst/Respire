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
    internal static bool IsCursorContinuation<TCommand>(in TCommand command)
        where TCommand : struct, IRespCommand
    {
        var index = command.CursorArgumentIndex;
        if (index < 0 || !command.TryGetArgument(index, out var cursor)) return false;
        return !(cursor.TryGetInt64(out var value) && value == 0);
    }
}
