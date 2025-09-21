using Respire.Commands;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// Interface for command queue implementations
/// </summary>
public interface IRespireCommandQueue : IAsyncDisposable
{
    /// <summary>
    /// Queues a command for execution (fire-and-forget)
    /// </summary>
    ValueTask QueueCommandAsync(
        CommandData command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a command and waits for its response
    /// </summary>
    ValueTask<RespireValue> QueueCommandWithResponseAsync(
        CommandData command,
        CancellationToken cancellationToken = default);
}