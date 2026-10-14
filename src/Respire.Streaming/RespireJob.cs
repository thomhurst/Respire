namespace Respire.Streaming;

/// <summary>A typed job delivery. A recovered delivery retains both its application ID and stream entry ID.</summary>
/// <typeparam name="T">The application payload type.</typeparam>
public sealed record RespireJob<T>(string Id, T Payload, RespireStreamId EntryId);

/// <summary>The stream entry for an enqueue attempt, including whether this call appended it.</summary>
/// <param name="EntryId">The new entry ID, or the original entry ID when deduplicated.</param>
/// <param name="Enqueued">False when the job ID was already enqueued within its deduplication window.</param>
public readonly record struct RespireJobEnqueueResult(RespireStreamId EntryId, bool Enqueued);
