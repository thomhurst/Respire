using System.Security.Cryptography;

namespace Respire.Streaming;

public sealed partial class RespireJobQueue<T>
{
    /// <summary>Creates an open batch, returning its ID. An existing ID retains its original callbacks.</summary>
    /// <remarks>Supply a stable batchId to retry an ambiguous request safely. A null ID generates a new ID
    /// for each call. Batches retain state until RemoveBatchAsync; reserve their keys against eviction or edits.
    /// Each batch supports at most 256 members and 256 KiB of combined job IDs and serialized payloads,
    /// including callbacks. Callback execution and external effects are at least once, not exactly once.</remarks>
    public async ValueTask<string> CreateBatchAsync(RespireJobBatchOptions<T>? options = null,
        string? batchId = null, CancellationToken cancellationToken = default)
    {
        batchId ??= Guid.NewGuid().ToString("N");
        var key = GetBatchKey(batchId);
        JobQueueScheduling.ValidateKeys(_client, [StreamKey, key]);
        cancellationToken.ThrowIfCancellationRequested();
        using var trace = new JobEnqueueTrace();
        var arguments = new List<RespireValue> { key, trace.Parent, trace.State };
        long inputBytes = 0;
        foreach (var callback in new[] { options?.OnSuccess, options?.OnFailure, options?.OnComplete })
        {
            if (callback is null) { arguments.Add(""); arguments.Add(""); continue; }
            var payload = SerializeBatchJob(callback.Id, callback.Payload, out var size);
            inputBytes += size;
            if (inputBytes > JobQueueBatches.MaximumInputBytes)
                throw new ArgumentException("Batch IDs and payloads must not exceed 256 KiB.", nameof(options));
            arguments.Add(callback.Id);
            arguments.Add(payload);
        }
        await _client.Scripts.ExecuteIntegerAsync(JobQueueBatches.Create, [key], [.. arguments], cancellationToken).ConfigureAwait(false);
        trace.Succeeded = true;
        return batchId;
    }

    /// <summary>Adds a runnable member to an open batch. Returns false for an existing member ID.</summary>
    /// <remarks>Member IDs are scoped to the batch, independently of ordinary enqueue deduplication.
    /// Retrying the same ID preserves its first payload, even after sealing or completion.</remarks>
    public async ValueTask<bool> AddBatchJobAsync(string batchId, string jobId, T payload,
        CancellationToken cancellationToken = default)
    {
        var key = GetBatchKey(batchId);
        JobQueueScheduling.ValidateKeys(_client, [StreamKey, key]);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = SerializeBatchJob(jobId, payload, out _);
        return await _client.Scripts.ExecuteIntegerAsync(JobQueueBatches.Add, [StreamKey, key],
            [jobId, bytes], cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Closes membership and publishes eligible callbacks if all members are already terminal.</summary>
    /// <remarks>Idempotent. Sealing an empty batch publishes its success and completion callbacks.</remarks>
    public ValueTask SealBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => ChangeBatchAsync(batchId, false, cancellationToken);

    /// <summary>Seals the batch and asks workers to skip remaining members before invoking their handlers.</summary>
    /// <remarks>Idempotent; a completed batch is unchanged. Running handlers are not interrupted. Members
    /// completing after cancellation count as cancelled, even if their handler ran. Workers must drain pending
    /// and delayed retries before failure/completion callbacks can be published. Callback jobs are not cancelled.</remarks>
    public ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => ChangeBatchAsync(batchId, true, cancellationToken);

    private async ValueTask ChangeBatchAsync(string batchId, bool cancel, CancellationToken cancellationToken)
    {
        var key = GetBatchKey(batchId);
        JobQueueScheduling.ValidateKeys(_client, [StreamKey, key]);
        await _client.Scripts.ExecuteIntegerAsync(JobQueueBatches.Seal, [StreamKey, key],
            [cancel ? 1 : 0], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads retained counts and state, or null for an unknown batch.</summary>
    public async ValueTask<RespireJobBatchStatus?> GetBatchStatusAsync(string batchId,
        CancellationToken cancellationToken = default)
    {
        using var result = await _client.Scripts.ExecuteAsync(JobQueueBatches.Read, [GetBatchKey(batchId)], [],
            cancellationToken).ConfigureAwait(false);
        return result.Count == 0 ? null : new((int)result[0].AsInteger(), (int)result[1].AsInteger(),
            (int)result[2].AsInteger(), (int)result[3].AsInteger(), result[4].AsInteger() == 1,
            result[5].AsInteger() == 1, result[6].AsInteger() == 1);
    }

    /// <summary>Removes a completed batch's retained state and ID reservation; false if absent.</summary>
    /// <remarks>Throws RespireServerException for an active batch. Callback jobs and events are retained
    /// independently; completion means publication, not callback execution. Reusing the ID creates a new run.</remarks>
    public async ValueTask<bool> RemoveBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => await _client.Scripts.ExecuteIntegerAsync(JobQueueBatches.Remove, [GetBatchKey(batchId)], [],
            cancellationToken).ConfigureAwait(false) == 1;

    private string GetBatchKey(string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        return StreamKey + ":batch:" + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(batchId)));
    }

    private byte[] SerializeBatchJob(string jobId, T payload, out long size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var idBytes = StrictUtf8.GetByteCount(jobId);
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null.");
        size = (long)idBytes + bytes.Length;
        if (size > JobQueueBatches.MaximumInputBytes)
            throw new ArgumentException("Batch IDs and payloads must not exceed 256 KiB.", nameof(payload));
        return bytes;
    }
}
