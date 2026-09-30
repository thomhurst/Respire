namespace Respire.Testing;

/// <summary>A controlled pause shared by one or more fake-server fault rules.</summary>
/// <remarks>Release is permanent and idempotent. Create a new gate for another pause.</remarks>
public sealed class RespireFakeGate
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Released => _released.Task;
    /// <summary>Allows all current and future waiters to proceed.</summary>
    public void Release() => _released.TrySetResult();
}

/// <summary>An immutable fault action for a complete RESP command.</summary>
public sealed class RespireFakeFault
{
    internal enum ActionKind { Delay, Pause, Disconnect, Error }
    internal ActionKind Kind { get; }
    internal bool AfterExecution { get; }
    internal TimeSpan Duration { get; }
    internal RespireFakeGate? Gate { get; }
    internal string? Error { get; }

    private RespireFakeFault(ActionKind kind, bool afterExecution = false, TimeSpan duration = default,
        RespireFakeGate? gate = null, string? error = null)
        => (Kind, AfterExecution, Duration, Gate, Error) = (kind, afterExecution, duration, gate, error);

    /// <summary>Delays execution or its reply using wall-clock time, independently of the expiry clock.</summary>
    public static RespireFakeFault Delay(TimeSpan duration, bool afterExecution = false)
    {
        if (duration < TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return new(ActionKind.Delay, afterExecution, duration);
    }

    /// <summary>Pauses execution or its reply until the gate is released or the rule is removed.</summary>
    public static RespireFakeFault Pause(RespireFakeGate gate, bool afterExecution = false)
        => new(ActionKind.Pause, afterExecution, gate: gate ?? throw new ArgumentNullException(nameof(gate)));

    /// <summary>Closes the connection without a reply, either before execution or after executing once.</summary>
    public static RespireFakeFault Disconnect(bool afterExecution = false) => new(ActionKind.Disconnect, afterExecution);

    /// <summary>Rejects the command before execution with a LOADING error.</summary>
    public static RespireFakeFault Loading() => new(ActionKind.Error, error: "LOADING Redis is loading the dataset in memory");

    /// <summary>Rejects the command before execution with a READONLY error.</summary>
    public static RespireFakeFault ReadOnly() => new(ActionKind.Error, error: "READONLY You can't write against a read only replica.");

    /// <summary>Rejects the command with MOVED. The fake does not provide Cluster topology or route to the destination.</summary>
    public static RespireFakeFault Moved(int slot, RespireEndpoint destination)
    {
        if ((uint)slot >= 16384) throw new ArgumentOutOfRangeException(nameof(slot));
        if (string.IsNullOrWhiteSpace(destination.Host) || destination.Host.Any(char.IsWhiteSpace) || destination.Host.Any(char.IsControl)
            || destination.Port is < 1 or > 65535)
            throw new ArgumentException("A valid redirect endpoint is required.", nameof(destination));
        return new(ActionKind.Error, error: $"MOVED {slot} {destination}");
    }
}
