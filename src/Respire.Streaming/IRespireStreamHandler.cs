namespace Respire.Streaming;

/// <summary>Handles one stream delivery within its own dependency-injection scope.</summary>
/// <typeparam name="TMessage">The deserialized message type.</typeparam>
public interface IRespireStreamHandler<in TMessage>
{
    /// <summary>Returns Ack after success, Nack for retry, or DeadLetter for atomic dead-letter completion.</summary>
    ValueTask<RespireStreamWorkerResult> HandleAsync(TMessage message, CancellationToken cancellationToken);
}

/// <summary>Explicit completion of a stream delivery.</summary>
public enum RespireStreamWorkerResult
{
    /// <summary>Retry after the configured minimum idle time, or dead-letter when the delivery limit is reached.</summary>
    Nack = 0,
    /// <summary>Acknowledge the entry after the handler completes successfully.</summary>
    Ack = 1,
    /// <summary>Atomically append the original entry to the configured dead-letter stream and acknowledge this attempt.</summary>
    /// <remarks>Requires <see cref="RespireStreamWorkerOptions.DeadLetterStream"/>. Without a destination,
    /// the worker logs a handler failure and leaves this delivery pending for retry, like Nack.</remarks>
    DeadLetter = 2,
}
