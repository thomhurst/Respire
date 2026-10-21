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
        // Binary cursors are opaque: only the exact ASCII terminator starts a scan.
        if (cursor.TryGetByteMemory(out var bytes)) return !bytes.Span.SequenceEqual("0"u8);
        return !(cursor.TryGetInt64(out var value) && value == 0);
    }

    /// <summary>
    /// True when a cursor read carries a server-issued cursor argument, so a retired connection must
    /// not move it to another server. <c>ARSCAN</c> pages are index ranges with no server cursor.
    /// </summary>
    internal static bool HasServerCursor<TCommand>(in TCommand command)
        where TCommand : struct, IRespCommand
        => command.ReadKind == ReadCommandKind.CursorRead && command.CursorArgumentIndex >= 0;

    /// <summary>
    /// True when a command continues a server-issued cursor, so a retired connection must not replay
    /// it on another server. A fresh scan (cursor <c>0</c>) can start anywhere.
    /// </summary>
    internal static bool IsServerCursorContinuation<TCommand>(in TCommand command)
        where TCommand : struct, IRespCommand
        => HasServerCursor(in command) && IsCursorContinuation(in command);
}
