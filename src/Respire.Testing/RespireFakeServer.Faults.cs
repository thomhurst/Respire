namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private readonly List<RespireFakeFaultScope> _faults = [];

    /// <summary>Installs a fault. The first available matching rule wins, in registration order.</summary>
    /// <remarks>Command names ignore case. FirstArgument optionally matches exact bytes at argument index one,
    /// which is not necessarily a key. Matching is atomic across connections. Occurrences defaults to one;
    /// null repeats until disposal/reset. A rule is selected once per complete command, never per fragment.
    /// Removing a rule does not undo a selected rejection/disconnect; it releases its delays/pauses.</remarks>
    public RespireFakeFaultScope InjectFault(string command, RespireFakeFault fault, int? occurrences = 1,
        ReadOnlyMemory<byte>? firstArgument = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Any(char.IsWhiteSpace) || command.Any(char.IsControl))
            throw new ArgumentException("Specify one command name without whitespace or controls.", nameof(command));
        ArgumentNullException.ThrowIfNull(fault);
        if (occurrences is <= 0) throw new ArgumentOutOfRangeException(nameof(occurrences));
        var scope = new RespireFakeFaultScope(this, command.ToUpperInvariant(), firstArgument?.ToArray(), fault, occurrences);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _faults.Add(scope);
        }
        return scope;
    }

    /// <summary>Removes every current fault rule and releases selected delays/pauses without clearing server data.</summary>
    public void ResetFaults()
    {
        RespireFakeFaultScope[] faults;
        lock (_gate)
        {
            faults = _faults.ToArray();
            _faults.Clear();
        }
        foreach (var fault in faults) fault.Dispose();
    }

    internal void RemoveFault(RespireFakeFaultScope scope)
    {
        lock (_gate) _faults.Remove(scope);
    }

    private RespireFakeFaultScope? MatchFault(byte[][] arguments)
    {
        var command = Token(arguments[0]);
        lock (_gate)
        {
            foreach (var scope in _faults)
            {
                if (!scope.Available || scope.Command != command
                    || scope.FirstArgument is { } first && (arguments.Length < 2 || !first.AsSpan().SequenceEqual(arguments[1])))
                    continue;
                scope.Reserve();
                return scope;
            }
            return null;
        }
    }

    private static async Task ApplyWaitAsync(Connection connection, RespireFakeFaultScope scope)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(connection.Lifetime.Token, scope.Removal);
        try
        {
            if (scope.Fault.Kind == RespireFakeFault.ActionKind.Delay)
                await Task.Delay(scope.Fault.Duration, stop.Token).ConfigureAwait(false);
            else if (scope.Fault.Kind == RespireFakeFault.ActionKind.Pause)
                await scope.Fault.Gate!.Released.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scope.Removal.IsCancellationRequested && !connection.Lifetime.IsCancellationRequested)
        {
            // Removing a rule releases waiting commands. Closing the connection aborts them.
        }
        connection.Lifetime.Token.ThrowIfCancellationRequested();
    }

    private async Task<Outbound?> ExecuteWithFaultAsync(Connection connection, byte[][] arguments)
    {
        var scope = MatchFault(arguments);
        var fault = scope?.Fault;
        if (fault is { AfterExecution: false })
        {
            scope!.ObserveMatch();
            if (fault.Kind == RespireFakeFault.ActionKind.Disconnect) return null;
            if (fault.Kind == RespireFakeFault.ActionKind.Error)
            {
                lock (_gate)
                {
                    var rejection = QueueOutputLocked(connection, RejectCommand(connection, Token(arguments[0]), FakeReply.Error(fault.Error!)).Encode(connection.Resp3), push: false);
                    rejection?.Ready.TrySetResult();
                    return rejection;
                }
            }
            await ApplyWaitAsync(connection, scope).ConfigureAwait(false);
        }
        connection.Lifetime.Token.ThrowIfCancellationRequested();
        var reply = Token(arguments[0]) switch
        {
            "BLMOVEM" => await ExecuteBlockingListMoveAsync(connection, arguments, scope).ConfigureAwait(false),
            "XREAD" or "XREADGROUP" => await ExecuteStreamReadAsync(connection, arguments, scope).ConfigureAwait(false),
            _ => ExecuteLocked(connection, arguments, scope, out _),
        };
        if (reply is null) return null;
        if (fault is { AfterExecution: true })
        {
            scope!.ObserveMatch();
            if (fault.Kind == RespireFakeFault.ActionKind.Disconnect) return null;
            await ApplyWaitAsync(connection, scope).ConfigureAwait(false);
        }
        reply.Ready.TrySetResult();
        return reply;
    }
}
