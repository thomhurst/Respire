namespace Respire.Streaming;

/// <summary>Handles one stream delivery within its own dependency-injection scope.</summary>
/// <typeparam name="TMessage">The deserialized message type.</typeparam>
public interface IRespireStreamHandler<in TMessage>
{
    /// <summary>Returns Ack only after successful processing; Nack leaves the delivery pending.</summary>
    ValueTask<RespireStreamWorkerResult> HandleAsync(TMessage message, CancellationToken cancellationToken);
}

/// <summary>Explicit completion of a stream delivery.</summary>
public enum RespireStreamWorkerResult
{
    /// <summary>Leave the entry pending for retry after the configured minimum idle time.</summary>
    Nack = 0,
    /// <summary>Acknowledge the entry after the handler completes successfully.</summary>
    Ack = 1,
}
