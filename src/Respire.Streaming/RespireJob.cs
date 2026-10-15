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
/// <param name="Enqueued">False when the job ID was already enqueued within its deduplication window.</param>
public readonly record struct RespireJobEnqueueResult(RespireStreamId EntryId, bool Enqueued);
