namespace Respire.Internal;

// Allocated only by the configured branches. Never invokes application code in a transport gate.
internal sealed class PipelineHookScope : IDisposable
{
    private readonly RespirePipelineHook _hook;
    private readonly RespirePipelineContext _context;
    private object? _state;
    private RespirePipelineOutcome _outcome;
    private Exception? _error;
    private bool _entered;

    internal PipelineHookScope(RespirePipelineHook hook, bool isTransaction, string[] commands,
        CancellationToken cancellationToken)
    {
        _hook = hook;
        _context = new(isTransaction, commands, cancellationToken);
    }

    internal void Enter()
    {
        _entered = true;
        try { _state = _hook.OnExecuting(_context); }
        catch (Exception error)
        {
            _outcome = RespirePipelineOutcome.Rejected;
            _error = error;
            throw;
        }
    }

    internal void Fail(Exception? error)
    {
        if (error is null || _error is not null) return;
        _error = error;
        _outcome = error is OperationCanceledException ? RespirePipelineOutcome.Canceled : RespirePipelineOutcome.Failed;
    }

    internal void Abort() => _outcome = RespirePipelineOutcome.Aborted;

    public void Dispose()
    {
        if (!_entered) return;
        try { _hook.OnCompleted(_context, _state, _outcome, _error); }
        catch (Exception) { /* Observers cannot replace results or interfere with resource cleanup. */ }
    }
}
