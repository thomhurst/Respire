namespace Respire.Networking;

/// <summary>
/// The connection was already closed when this command tried to enqueue. No bytes were
/// appended to the write buffer and no reply slot was reserved, so the command cannot have
/// reached Redis.
/// </summary>
/// <remarks>
/// It is still a <see cref="RespireConnectionException"/> with the close reason's message and
/// cause, so ordinary callers see the same failure as before. Lock release uses the type as
/// proof that no delete was sent. Specific close reasons (authentication, reconnect limit,
/// protocol faults) are rethrown unchanged instead; a release that hits one of those still
/// fails closed.
/// </remarks>
internal sealed class RespireConnectionClosedBeforeSendException(string message, Exception? innerException)
    : RespireConnectionException(message, innerException!);
