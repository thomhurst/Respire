namespace Respire.Streaming;

/// <summary>A typed job delivery. A recovered delivery retains both its application ID and stream entry ID.</summary>
/// <typeparam name="T">The application payload type.</typeparam>
public sealed record RespireJob<T>(string Id, T Payload, RespireStreamId EntryId) : IJobDelivery, IJobRetry
{
    /// <summary>The application group identity from enqueue, or null for an ungrouped job.</summary>
    /// <remarks>This is envelope metadata, not the Redis consumer group or a scheduling guarantee.</remarks>
    public string? Group { get; init; }

    private TimeSpan? _retryDelay;
    TimeSpan? IJobRetry.RetryDelay => _retryDelay;

    private JobProgress? _progress;
    private byte[]? _result;
    byte[]? IJobDelivery.Result => _result;

    void IJobDelivery.Attach(JobProgress progress) => _progress = progress;

    /// <summary>Reads the last durable checkpoint, or null before the first save.</summary>
    public ValueTask<RespireJobCheckpoint?> ReadCheckpointAsync(CancellationToken cancellationToken = default)
        => (_progress ?? throw new InvalidOperationException("Checkpoints require a hosted resumable job."))
            .ReadCheckpointAsync(cancellationToken);

    /// <summary>Saves a cursor, up to 4096 state bytes and optional progress. False means this delivery lost ownership or expired.</summary>
    /// <remarks>The cursor must fit 1024 UTF-8 bytes. Await saves before returning. On cancellation,
    /// use a separate bounded token to save the final checkpoint before leaving the handler.</remarks>
    public ValueTask<bool> SaveCheckpointAsync(string cursor, byte[] state, int? percent = null,
        CancellationToken cancellationToken = default)
        => (_progress ?? throw new InvalidOperationException("Checkpoints require a hosted resumable job."))
            .SaveCheckpointAsync(cursor, state, percent, cancellationToken);

    /// <summary>Returns Ack with an owned copy of a binary result for this flow node.</summary>
    /// <remarks>Return this from the handler. Only a current, successful flow delivery persists the result.
    /// Ordinary jobs do not retain results. Results must not exceed 4 KiB; flow state is rewritten on each completion.</remarks>
    public RespireStreamWorkerResult Complete(byte[] result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Length > JobQueueFlows.MaximumResultBytes)
            throw new ArgumentOutOfRangeException(nameof(result), "Job results must not exceed 4096 bytes.");
        _result = result.ToArray();
        return RespireStreamWorkerResult.Ack;
    }

    /// <summary>Reads retained direct-child results for a hosted flow delivery; ordinary jobs return an empty list.</summary>
    /// <remarks>Ignore policy omits failed children. Continue includes them with Status set to failed.</remarks>
    public ValueTask<IReadOnlyList<RespireJobFlowResult>> GetChildResultsAsync(CancellationToken cancellationToken = default)
        => (_progress ?? throw new InvalidOperationException("Child results require a hosted job delivery."))
            .ReadChildrenAsync(cancellationToken);

    /// <summary>Compares the job envelope, excluding per-delivery progress, result and retry control state.</summary>
    public bool Equals(RespireJob<T>? other) => other is not null
        && Id == other.Id && EqualityComparer<T>.Default.Equals(Payload, other.Payload)
        && EntryId == other.EntryId && Group == other.Group;

    /// <summary>Hashes the job envelope, excluding per-delivery progress, result and retry control state.</summary>
    public override int GetHashCode() => HashCode.Combine(Id, Payload, EntryId, Group);

    /// <summary>Appends progress from 0 through 100 for this delivery; false means it is no longer current.</summary>
    /// <remarks>Only hosted deliveries can report progress. Reports do not extend the visibility timeout.
    /// Await reports before returning from the handler. A lost reply can leave a duplicate progress event on retry.</remarks>
    public ValueTask<bool> ReportProgressAsync(int percent, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        return (_progress ?? throw new InvalidOperationException("Progress requires a hosted job delivery."))
            .ReportAsync(percent, cancellationToken);
    }

    /// <summary>Appends a bounded log line for a hosted delivery; false means it is stale or expired.</summary>
    /// <remarks>Await logging before returning from the handler. Lines must be valid Unicode and fit the queue's byte limit.
    /// Logs share the application job ID across retries. An uncertain reply can leave a duplicate line on retry.</remarks>
    public ValueTask<bool> LogAsync(string message, CancellationToken cancellationToken = default)
        => (_progress ?? throw new InvalidOperationException("Logging requires a hosted job delivery."))
            .LogAsync(message, cancellationToken);

    /// <summary>Returns Nack and requests a delay from completion, overriding the queue backoff for this attempt.</summary>
    /// <remarks>Return this result from the handler. The delivery limit still applies. Zero allows immediate retry.</remarks>
    public RespireStreamWorkerResult RetryAfter(TimeSpan delay)
    {
        JobQueueScheduling.ValidateDuration(delay, nameof(delay), allowZero: true);
        _retryDelay = delay;
        return RespireStreamWorkerResult.Nack;
    }
}

/// <summary>The stream entry for an enqueue attempt, including whether this call appended it.</summary>
/// <param name="EntryId">The new entry ID, or the original entry ID when deduplicated.</param>
/// <param name="Enqueued">True when a new job or pending Replace envelope was accepted; false when suppressed.</param>
public readonly record struct RespireJobEnqueueResult(RespireStreamId EntryId, bool Enqueued);
