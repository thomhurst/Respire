namespace Respire;

/// <summary>Base exception for all Respire failures.</summary>
public class RespireException : Exception
{
    public RespireException(string message) : base(message)
    {
    }

    public RespireException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The connection failed, was closed by the peer, or was disposed with commands in flight.</summary>
public class RespireConnectionException : RespireException
{
    public RespireConnectionException(string message) : base(message)
    {
    }

    public RespireConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The byte stream violated the RESP protocol; the connection is no longer usable.</summary>
public sealed class RespireProtocolException : RespireException
{
    public RespireProtocolException(string message) : base(message)
    {
    }

    public RespireProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Respire configuration prevents the requested operation from being used.</summary>
public sealed class RespireConfigurationException : RespireException
{
    public RespireConfigurationException(string message) : base(message)
    {
    }

    public RespireConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A distributed lock could not be acquired because another owner holds it.</summary>
public sealed class RespireLockNotAcquiredException() : RespireException(
    "The distributed lock was not acquired because another owner holds it.");

/// <summary>An unsent batch was disposed, so none of its queued commands ran.</summary>
public sealed class RespireBatchDiscardedException() : InvalidOperationException(
    "The batch was disposed before SendAsync; its queued commands were discarded.");

/// <summary>A deferred result was read before its batch or transaction executed.</summary>
public sealed class RespirePendingNotReadyException() : InvalidOperationException(
    "This result is not available yet: send the batch (SendAsync) or commit the transaction (CommitAsync) first.");

/// <summary>A watched key changed, so Redis aborted the transaction and no command ran.</summary>
public sealed class RespireTransactionAbortedException() : InvalidOperationException(
    "The transaction was aborted — a watched key changed, so no command ran.");

/// <summary>The server answered a command with a RESP error reply ("-WRONGTYPE ...").</summary>
public sealed class RespireServerException : RespireException
{
    public RespireServerException(string message) : this(message, commandName: null)
    {
    }

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
    public const string Err = "ERR";
    public const string WrongType = "WRONGTYPE";
    public const string NoScript = "NOSCRIPT";
    public const string BusyGroup = "BUSYGROUP";
    public const string NoAuth = "NOAUTH";
    public const string NoPerm = "NOPERM";
    public const string Loading = "LOADING";
    public const string Busy = "BUSY";
    public const string ClusterDown = "CLUSTERDOWN";
    public const string TryAgain = "TRYAGAIN";
    public const string MasterDown = "MASTERDOWN";
    public const string Moved = "MOVED";
    public const string Ask = "ASK";
    public const string ReadOnly = "READONLY";
    public const string Oom = "OOM";
    public const string ExecAbort = "EXECABORT";
    public const string CrossSlot = "CROSSSLOT";
}

/// <summary>
/// A command's response did not arrive within <see cref="RespireOptions.CommandTimeout"/>. The
/// timeout covers waiting for the response only — the command was already sent and may still
/// execute on the server.
/// </summary>
public sealed class RespireTimeoutException : RespireException
{
    public RespireTimeoutException(string commandName, TimeSpan timeout)
        : base(CreateMessage(commandName, timeout))
    {
        CommandName = commandName;
        Timeout = timeout;
    }

    public RespireTimeoutException(string commandName, TimeSpan timeout, Exception innerException)
        : base(CreateMessage(commandName, timeout), innerException)
    {
        CommandName = commandName;
        Timeout = timeout;
    }

    public string CommandName { get; }

    public TimeSpan Timeout { get; }

    private static string CreateMessage(string commandName, TimeSpan timeout)
        => $"{commandName} timed out after {timeout.TotalMilliseconds:0}ms. The command was sent and may still " +
           "execute on the server; only the wait was abandoned. If this recurs, check server load and slow " +
           $"commands (SLOWLOG), network latency, and whether {nameof(RespireOptions)}.{nameof(RespireOptions.CommandTimeout)} is realistic.";
}
