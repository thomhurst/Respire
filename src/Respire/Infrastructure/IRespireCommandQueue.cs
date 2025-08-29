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
        Func<Protocol.PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Queues a command and waits for its response
    /// </summary>
    ValueTask<RespireValue> QueueCommandWithResponseAsync(
        Func<Protocol.PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default);
}