namespace Respire;

/// <summary>Base exception for all Respire failures.</summary>
public class RespireException : Exception
{
    /// <summary>Creates a Respire exception.</summary>
    public RespireException(string message) : base(message)
    {
    }

    /// <summary>Creates a Respire exception with its underlying cause.</summary>
    public RespireException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The connection failed, was closed by the peer, or was disposed with commands in flight.</summary>
public class RespireConnectionException : RespireException
{
    /// <summary>Creates a connection exception.</summary>
    public RespireConnectionException(string message) : base(message)
    {
    }

    /// <summary>Creates a connection exception with its underlying cause.</summary>
    public RespireConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Credential acquisition, renewal, or authentication failed.</summary>
public sealed class RespireAuthenticationException : RespireConnectionException
{
    /// <summary>Creates a credential acquisition, renewal, or authentication failure.</summary>
    public RespireAuthenticationException(string message) : base(message) { }

    /// <summary>Creates an authentication failure with its underlying cause.</summary>
    public RespireAuthenticationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A connection recovery episode exhausted its configured replacement attempts.</summary>
public sealed class RespireReconnectLimitException : RespireConnectionException
{
    /// <summary>Creates a recovery limit exception.</summary>
    public RespireReconnectLimitException(string message) : base(message) { }

    /// <summary>Creates a recovery limit exception with the last connection failure.</summary>
    public RespireReconnectLimitException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The byte stream violated the RESP protocol; the connection is no longer usable.</summary>
public sealed class RespireProtocolException : RespireException
{
    /// <summary>Creates a protocol exception.</summary>
    public RespireProtocolException(string message) : base(message)
    {
    }

    /// <summary>Creates a protocol exception with its underlying cause.</summary>
    public RespireProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Respire configuration prevents the requested operation from being used.</summary>
public sealed class RespireConfigurationException : RespireException
{
    /// <summary>Creates a configuration exception.</summary>
    public RespireConfigurationException(string message) : base(message)
    {
    }

    /// <summary>Creates a configuration exception with its underlying cause.</summary>
    public RespireConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A distributed lock could not be acquired because another owner holds it.</summary>
public sealed class RespireLockNotAcquiredException() : RespireException(
    "The distributed lock was not acquired because another owner holds it.");

/// <summary>An unsent batch was disposed, so none of its queued commands ran.</summary>
public sealed class RespireBatchDiscardedException() : RespireException(
    "The batch was disposed before ExecuteAsync; its queued commands were discarded.");

/// <summary>An uncommitted transaction was disposed, so none of its queued commands ran.</summary>
public sealed class RespireTransactionDiscardedException() : RespireException(
    "The transaction was disposed before CommitAsync; its queued commands were discarded.");

/// <summary>A deferred result was read before its batch or transaction executed.</summary>
public sealed class RespirePendingNotReadyException() : RespireException(
    "This result is not available yet: execute the batch (ExecuteAsync) or commit the transaction (CommitAsync) first.");

/// <summary>A watched key changed, so Redis aborted the transaction and no command ran.</summary>
public sealed class RespireTransactionAbortedException() : RespireException(
    "The transaction was aborted — a watched key changed, so no command ran.");

/// <summary>Redis Cluster rejected a watched transaction because its slot owner changed or is migrating.</summary>
/// <remarks>No automatic replay occurs. Start a new watched transaction and re-read its inputs before retrying.
/// This exception is not used for an ambiguous connection failure or an error inside an executed result array.</remarks>
public sealed class RespireTransactionRetryException : RespireException
{
    internal RespireTransactionRetryException(RespireServerException serverError)
        : base("Redis Cluster rejected the watched transaction. Start a new WATCH attempt and re-read all inputs before retrying.", serverError)
        => ServerError = serverError;

    /// <summary>The original MOVED, ASK, or READONLY rejection.</summary>
    public RespireServerException ServerError { get; }
}

/// <summary>The server answered a command with a RESP error reply ("-WRONGTYPE ...").</summary>
public sealed class RespireServerException : RespireException
{
    /// <summary>Creates a server exception when the originating command is unknown.</summary>
    public RespireServerException(string message) : this(message, commandName: null)
    {
    }

    /// <summary>Creates a server exception for a named Redis command.</summary>
    public RespireServerException(string message, string? commandName) : base(message)
    {
        Code = ParseCode(message);
        CommandName = commandName;
    }

    /// <summary>
    /// The error's leading code token ("ERR", "WRONGTYPE", "NOSCRIPT", "BUSYGROUP", …), or an
    /// empty string when the reply had no recognizable code.
    /// </summary>
    public string Code { get; }

    /// <summary>The Redis command that produced the error, when known.</summary>
    public string? CommandName { get; }

    /// <summary>Whether retrying may succeed without changing the command.</summary>
    public bool IsTransient => Code is
        RespireErrorCodes.Loading or
        RespireErrorCodes.Busy or
        RespireErrorCodes.ClusterDown or
        RespireErrorCodes.TryAgain or
        RespireErrorCodes.MasterDown;

    private static string ParseCode(string message)
    {
        var end = message.IndexOf(' ');
        var token = end < 0 ? message : message[..end];
        foreach (var c in token)
        {
            if (c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_')
            {
                return string.Empty;
            }
        }

        return token;
    }
}

/// <summary>Known Redis error reply codes.</summary>
public static class RespireErrorCodes
{
    /// <summary>Generic server error.</summary>
    public const string Err = "ERR";
    /// <summary>Command used against the wrong data type.</summary>
    public const string WrongType = "WRONGTYPE";
    /// <summary>Referenced Lua script is not cached.</summary>
    public const string NoScript = "NOSCRIPT";
    /// <summary>Consumer group already exists.</summary>
    public const string BusyGroup = "BUSYGROUP";
    /// <summary>Authentication is required.</summary>
    public const string NoAuth = "NOAUTH";
    /// <summary>The authenticated user lacks permission.</summary>
    public const string NoPerm = "NOPERM";
    /// <summary>The server is loading data.</summary>
    public const string Loading = "LOADING";
    /// <summary>A script or function is busy.</summary>
    public const string Busy = "BUSY";
    /// <summary>The cluster is unavailable.</summary>
    public const string ClusterDown = "CLUSTERDOWN";
    /// <summary>The cluster asks the client to retry.</summary>
    public const string TryAgain = "TRYAGAIN";
    /// <summary>The master is unavailable.</summary>
    public const string MasterDown = "MASTERDOWN";
    /// <summary>Permanent Redis Cluster slot redirect.</summary>
    public const string Moved = "MOVED";
    /// <summary>Temporary Redis Cluster slot redirect.</summary>
    public const string Ask = "ASK";
    /// <summary>A replica rejected a write.</summary>
    public const string ReadOnly = "READONLY";
    /// <summary>The server rejected a write because of memory policy.</summary>
    public const string Oom = "OOM";
    /// <summary>A transaction was discarded because queueing failed.</summary>
    public const string ExecAbort = "EXECABORT";
    /// <summary>Keys do not hash to one Redis Cluster slot.</summary>
    public const string CrossSlot = "CROSSSLOT";
}

/// <summary>
/// A command did not complete within <see cref="RespireOptions.CommandTimeout"/>. Commands
/// already enqueued may still execute on the server. Inspect <see cref="Diagnostics"/> for
/// timeouts during connection acquisition or while waiting for queue capacity.
/// </summary>
public sealed class RespireTimeoutException : RespireException
{
    /// <summary>Creates a command timeout exception with an unavailable-connection snapshot.</summary>
    public RespireTimeoutException(string commandName, TimeSpan timeout)
        : this(commandName, timeout, null, RespireTimeoutDiagnostics.Unavailable) { }

    /// <summary>Creates a command timeout exception, preserving an underlying timeout snapshot.</summary>
    public RespireTimeoutException(string commandName, TimeSpan timeout, Exception innerException)
        : this(commandName, timeout, innerException,
            (innerException as RespireTimeoutException)?.Diagnostics ?? RespireTimeoutDiagnostics.Unavailable) { }

    internal RespireTimeoutException(string commandName, TimeSpan timeout, Exception? innerException,
        RespireTimeoutDiagnostics diagnostics)
        : base(CreateMessage(commandName, timeout, diagnostics), innerException!)
    {
        CommandName = commandName;
        Timeout = timeout;
        Diagnostics = diagnostics;
    }

    /// <summary>The Redis command whose response timed out.</summary>
    public string CommandName { get; }

    /// <summary>The response timeout that elapsed.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>A snapshot captured on the timeout path; unavailable connection fields are null.</summary>
    public RespireTimeoutDiagnostics Diagnostics { get; }

    private static string CreateMessage(string commandName, TimeSpan timeout, RespireTimeoutDiagnostics diagnostics)
        => $"{commandName} timed out after {timeout.TotalMilliseconds:0}ms. " +
           (diagnostics.Stage is RespireCommandStage.Connecting or RespireCommandStage.WaitingForCapacity
               ? "The command had not been enqueued on the inspected connection. "
               : "The command may still execute on the server; only the wait was abandoned. ") +
           diagnostics.Describe() + " Review RespireOptions.CommandTimeout if the observed latency is expected.";
}

/// <summary>
/// Cancellation observed while a command waited for in-flight capacity, before it was enqueued.
/// It is still an <see cref="OperationCanceledException"/> carrying the caller's token for ordinary
/// callers; lock release uses it as proof that no delete was sent. Every command cancelled in that
/// wait now surfaces this subtype, so code that matches the exact runtime type
/// (<c>GetType() == typeof(OperationCanceledException)</c>) instead of using <c>catch</c> or
/// <c>is</c> sees a different type; the library itself never does.
/// </summary>
internal sealed class RespireCommandNotSubmittedException(OperationCanceledException error)
    : OperationCanceledException(error.Message, error, error.CancellationToken);
