namespace Respire;

/// <summary>Opt-in callbacks around each physical connection establishment attempt.</summary>
/// <remarks>
/// One hook can serve concurrent attempts. Callbacks must cooperate with cancellation and must
/// not wait for operations on the client whose connection is being established.
/// </remarks>
public abstract class RespireConnectionHook
{
    /// <summary>Runs before credential acquisition or transport creation. Throw to reject the attempt.</summary>
    public virtual ValueTask OnConnectingAsync(RespireConnectionContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <summary>Runs once after the attempt, including failures and rejections from <see cref="OnConnectingAsync"/>.</summary>
    /// <remarks>
    /// Success includes TLS, authentication, protocol negotiation and connection-local setup;
    /// topology discovery and routing validation performed by callers are outside this boundary.
    /// Throwing on success rejects and disposes the connection. Throwing on failure does not replace
    /// the original error. The initiating token may already be canceled; use a separate bounded
    /// token for audit persistence that must survive cancellation. Elapsed time excludes this callback.
    /// </remarks>
    public virtual ValueTask OnCompletedAsync(RespireConnectionContext context, RespireConnectionOutcome outcome,
        TimeSpan elapsed, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

/// <summary>Metadata for one attempt; the same instance is passed to both callbacks.</summary>
/// <remarks>Contains no credentials, connection handle, command arguments, replies, or exception text.</remarks>
public sealed class RespireConnectionContext
{
    internal RespireConnectionContext(RespireEndpoint endpoint, bool useTls)
    {
        Endpoint = endpoint;
        UseTls = useTls;
    }

    /// <summary>The requested physical endpoint, before DNS resolution; Unix sockets use a path and port zero.</summary>
    public RespireEndpoint Endpoint { get; }

    /// <summary>Whether this attempt uses TLS, including any Sentinel-specific override.</summary>
    public bool UseTls { get; }
}

/// <summary>The establishment outcome before the completion callback runs.</summary>
public enum RespireConnectionOutcome
{
    /// <summary>Connection-local setup completed successfully.</summary>
    Succeeded,
    /// <summary>Establishment or the connecting callback failed, including timeouts and rejection.</summary>
    Failed,
    /// <summary>The attempt was canceled through its initiating cancellation token.</summary>
    Canceled,
}
