namespace Respire.Testing;

/// <summary>An installed fault rule and its observations. Dispose to remove it and release its pauses.</summary>
/// <remarks>Counts include only commands selected by this rule. ExecutionCount counts handler invocations,
/// not successful mutations. A matched command can still be abandoned when its connection or server closes.</remarks>
public sealed class RespireFakeFaultScope : IDisposable
{
    private readonly RespireFakeServer _server;
    private readonly TaskCompletionSource _matched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _removed = new();
    private int _disposed;
    private long _matchedCount;
    private long _executionCount;
    internal string Command { get; }
    internal byte[]? FirstArgument { get; }
    internal RespireFakeFault Fault { get; }
    internal int? Occurrences { get; }
    internal CancellationToken Removal => _removed.Token;
    internal bool Available => Volatile.Read(ref _disposed) == 0
        && (Occurrences is null || MatchedCount < Occurrences);

    internal RespireFakeFaultScope(RespireFakeServer server, string command, byte[]? firstArgument,
        RespireFakeFault fault, int? occurrences)
        => (_server, Command, FirstArgument, Fault, Occurrences) = (server, command, firstArgument, fault, occurrences);

    /// <summary>Completes when the first command reaches this fault's execution/reply boundary.</summary>
    /// <remarks>For an after-execution action, the handler has already returned when this completes.
    /// Removing a rule before that boundary cancels this task. Await with a caller deadline when a match is optional.</remarks>
    public Task Matched => _matched.Task;
    /// <summary>The number of complete commands reserved by this rule.</summary>
    public long MatchedCount => Interlocked.Read(ref _matchedCount);
    /// <summary>The number of selected commands whose server handler was invoked.</summary>
    public long ExecutionCount => Interlocked.Read(ref _executionCount);

    internal void Reserve() => Interlocked.Increment(ref _matchedCount);
    internal void ObserveMatch() => _matched.TrySetResult();
    internal void ObserveExecution() => Interlocked.Increment(ref _executionCount);

    /// <summary>Removes future matching and releases selected delays/pauses. It never reverses an executed command.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _server.RemoveFault(this);
        _removed.Cancel();
        _matched.TrySetCanceled(_removed.Token);
        // Selected commands may still register the token after removal. No timers or wait
        // handles are owned here; leave the cancelled source alive until those commands finish.
    }
}
