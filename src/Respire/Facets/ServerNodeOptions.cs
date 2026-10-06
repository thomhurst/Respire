using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>Selects persistence behavior for SHUTDOWN.</summary>
public enum RespireShutdownSaveMode
{
    /// <summary>Uses the server's configured persistence behavior.</summary>
    Default,
    /// <summary>Saves an RDB snapshot before shutdown.</summary>
    Save,
    /// <summary>Skips the RDB snapshot. Required to stop an unkillable BUSY script.</summary>
    NoSave,
}

/// <summary>Options for SHUTDOWN. NOW and FORCE require Redis 7.0 or later.</summary>
public sealed record RespireShutdownOptions
{
    /// <summary>Snapshot behavior.</summary>
    public RespireShutdownSaveMode SaveMode { get; init; }
    /// <summary>Does not wait for replicas to catch up.</summary>
    public bool Now { get; init; }
    /// <summary>Shuts down even if persistence fails. This can lose data.</summary>
    public bool Force { get; init; }
}

/// <summary>Options for coordinated FAILOVER (Redis 6.2 or later).</summary>
public sealed record RespireFailoverOptions
{
    /// <summary>Optional specific replica to promote. Must be a TCP endpoint.</summary>
    public RespireEndpoint? Target { get; init; }
    /// <summary>Optional positive time allowed for replication catch-up.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Promotes Target after Timeout even if it has not caught up. Requires both options.</summary>
    public bool Force { get; init; }
}

/// <summary>Options for MIGRATE. Credentials authenticate to the destination server.</summary>
public sealed record RespireMigrateOptions
{
    /// <summary>Leaves source keys in place after transfer.</summary>
    public bool Copy { get; init; }
    /// <summary>Replaces existing destination keys.</summary>
    public bool Replace { get; init; }
    /// <summary>Optional ACL username. Requires Password; emits AUTH2.</summary>
    public string? Username { get; init; }
    /// <summary>Optional destination password. Without Username, emits AUTH.</summary>
    public string? Password { get; init; }
}

/// <summary>The acknowledged MIGRATE result.</summary>
public enum RespireMigrateResult
{
    /// <summary>Keys were transferred successfully.</summary>
    Migrated,
    /// <summary>No requested key exists at the source.</summary>
    NoKey,
}

/// <summary>An owned binary key and its COMMAND GETKEYSANDFLAGS flags (Redis 7.0 or later).</summary>
public sealed record RespireCommandKeyFlags(
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Owned response bytes are retained without copying on access.")]
    byte[] Key,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Owned response flags are retained without copying on access.")]
    string[] Flags);

/// <summary>An owned BACKUP STATUS reply (Redis 8.10 or later).</summary>
public sealed record RespireBackupStatus(
    string State,
    string Error,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);
