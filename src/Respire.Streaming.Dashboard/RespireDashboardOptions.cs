namespace Respire.Streaming.Dashboard;

/// <summary>Configures the explicitly allowed streams visible to dashboard operators.</summary>
public sealed class RespireDashboardOptions
{
    /// <summary>The streams to inspect. No Redis key discovery is performed.</summary>
    public IList<RespireDashboardStream> Streams { get; } = new List<RespireDashboardStream>();

    /// <summary>Maximum pending entries, dead letters, application groups per page, and configured batch IDs
    /// per stream, from 1 to 200. Defaults to 50.</summary>
    public int EntryLimit { get; set; } = 50;

    /// <summary>Maximum one-minute history buckets displayed per job queue, from 1 to 1440. Defaults to 60.</summary>
    public int MetricsHistoryMinutes { get; set; } = 60;
}

/// <summary>A stream and consumer group that an operator may inspect and manage.</summary>
public sealed class RespireDashboardStream
{
    /// <summary>The display name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The logical source key, before the client's key prefix.</summary>
    public string Stream { get; set; } = "";
    /// <summary>The consumer group whose pending entries and delivery rate are displayed.</summary>
    public string Group { get; set; } = "";
    /// <summary>An optional same-slot dead-letter key, reserved exclusively for this source and group.</summary>
    public string? DeadLetterStream { get; set; }
    /// <summary>Whether retry resets reliable job attempt and expiry fields. Defaults to false for plain streams.</summary>
    public bool IsJobQueue { get; set; }

    /// <summary>Batch IDs to inspect in configuration order, at most EntryLimit. Requires IsJobQueue
    /// and the queue's logical StreamKey. IDs must be nonblank valid UTF-8, at most 4096 characters.
    /// No batch discovery is performed; registration freezes this list.</summary>
    public IList<string> BatchIds { get; } = new List<string>();
}
