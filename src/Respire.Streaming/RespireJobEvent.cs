using System.Globalization;

namespace Respire.Streaming;

/// <summary>A retained progress or terminal event. EntryId identifies an enqueue; Id is the observer cursor.</summary>
public sealed class RespireJobEvent
{
    internal RespireJobEvent(RespireStreamEntry entry)
    {
        Id = entry.Id;
        JobId = entry.GetString("job_id")!;
        EntryId = new(entry.GetString("entry_id")!);
        Attempt = long.Parse(entry.GetString("attempt")!, CultureInfo.InvariantCulture);
        Kind = entry.GetString("kind")!;
        Percent = int.TryParse(entry.GetString("percent"), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : null;
        Reason = entry.GetString("reason");
        FlowKey = entry.GetString("flow_key");
    }

    /// <summary>The event stream ID to persist after processing this event.</summary>
    public RespireStreamId Id { get; }
    /// <summary>The application job ID; use EntryId to distinguish reuse after deduplication expiry.</summary>
    public string JobId { get; }
    /// <summary>The ready-stream entry ID, or 0-0 for a flow parent failed without execution.</summary>
    public RespireStreamId EntryId { get; }
    /// <summary>The fenced delivery count, or zero for a flow parent failed without execution.</summary>
    public long Attempt { get; }
    /// <summary>One of progress, completed, or dead-letter.</summary>
    public string Kind { get; }
    /// <summary>Progress from 0 through 100, or null for terminal events.</summary>
    public int? Percent { get; }
    /// <summary>Dead-letter reason, or null for progress and successful completion.</summary>
    public string? Reason { get; }
    /// <summary>The logical retained flow-state key, or null for an ordinary job or progress event.</summary>
    public string? FlowKey { get; }
}
