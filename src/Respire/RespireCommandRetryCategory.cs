namespace Respire;

/// <summary>Audited risk of repeating a command after an uncertain execution outcome.</summary>
/// <remarks>
/// This metadata does not enable retries. Repeating a command cannot recover its original reply
/// and does not provide exactly-once execution. Except for <see cref="Never"/>, categories are
/// ordered by increasing retry risk. Option-sensitive commands use their most conservative
/// audited category until an execution policy can inspect the complete invocation.
/// Numeric ordering is descriptive, not a retry policy contract. A policy must inspect the
/// named category, complete invocation and transport outcome rather than compare numeric thresholds.
/// </remarks>
public enum RespireCommandRetryCategory : byte
{
    /// <summary>No automatic retry permission, including unknown and caller-supplied commands.</summary>
    Never = 0,
    /// <summary>A stateless operation such as PING or ECHO.</summary>
    Always = 1,
    /// <summary>An audited connection or server-information operation.</summary>
    Connection = 2,
    /// <summary>Reads data without consuming server-side state.</summary>
    ReadOnly = 3,
    /// <summary>A conditional write whose repeated condition can prevent repeating its effect.</summary>
    WriteChecked = 4,
    /// <summary>A write that replaces a value; repetition can overwrite an intervening change.</summary>
    WriteLastWins = 5,
    /// <summary>A write that can accumulate effects, consume data, or lose its original result.</summary>
    WriteAccumulating = 6,
    /// <summary>An administrative mutation with server-wide consequences.</summary>
    ServerAdmin = 7,
}
