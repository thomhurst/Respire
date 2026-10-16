namespace Respire.Coordination;

/// <summary>Lease, retry, and callback settings for a hosted leader election.</summary>
public sealed class RespireLeaderElectionOptions
{
    /// <summary>The server lease duration. Defaults to 30 seconds; renewal runs halfway through the lease.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay between unsuccessful attempts or completed terms. Defaults to one second.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Runs the leader's work. Returning relinquishes leadership; throwing faults the hosted service.</summary>
    /// <remarks>Observe the term's cancellation token and fence downstream writes. Do not leave detached work running.</remarks>
    public required Func<RespireLeadership, Task> OnElected { get; init; }

    /// <summary>Called once after each elected term is cancelled, including shutdown and callback failure.</summary>
    /// <remarks>May run concurrently with OnElected while it drains. Both callbacks must finish before this instance campaigns again.</remarks>
    public Func<RespireLeadership, Task>? OnRevoked { get; init; }
}

/// <summary>An immutable leadership term; its token is cancelled when ownership becomes uncertain or work ends.</summary>
public sealed class RespireLeadership
{
    internal RespireLeadership(long fencingToken, CancellationToken cancellationToken)
        => (FencingToken, CancellationToken) = (fencingToken, cancellationToken);

    /// <summary>The fencing token to validate atomically with each downstream write.</summary>
    public long FencingToken { get; }

    /// <summary>Cancelled on renewal failure, local expiry, shutdown, or completion of leader work.</summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>A local transition into or out of an elected term.</summary>
public sealed class RespireLeadershipChangedEventArgs : EventArgs
{
    internal RespireLeadershipChangedEventArgs(RespireLeadership leadership, bool isLeader)
        => (Leadership, IsLeader) = (leadership, isLeader);

    /// <summary>The term whose state changed.</summary>
    public RespireLeadership Leadership { get; }

    /// <summary>True on election; false on revocation.</summary>
    public bool IsLeader { get; }
}
