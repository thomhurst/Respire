namespace Respire.Internal;

// Opt-in execution state only: deferred operations keep their existing allocation size.
// Admission finishes before transport starts; completion reads final converted pendings
// after the aggregate's existing fences, leases and import usage have been released.
internal sealed class AggregateCommandHookScope : IDisposable
{
    private readonly RespireCommandHook _hook;
    private readonly IReadOnlyList<DeferredOperation> _operations;
    private readonly Entry[] _entries;
    private readonly CancellationToken _token;
    private readonly string? _acknowledgement;
    private int _entered;
    private Exception? _acknowledgementError;

    private struct Entry
    {
        internal RespireCommandContext Context;
        internal object? State;
        internal Exception? Rejection;
    }

    internal AggregateCommandHookScope(RespireCommandHook hook,
        IReadOnlyList<DeferredOperation> operations, CancellationToken token, string? acknowledgement = null)
    {
        _hook = hook;
        _operations = operations;
        _token = token;
        _acknowledgement = acknowledgement;
        _entries = new Entry[operations.Count + (acknowledgement is null ? 0 : 1)];
    }

    internal void Enter()
    {
        for (var i = 0; i < _entries.Length; i++)
        {
            ref var entry = ref _entries[i];
            entry.Context = new(i < _operations.Count ? _operations[i].Operation : _acknowledgement!, _token);
            _entered++;
            try { entry.State = _hook.OnExecuting(entry.Context); }
            catch (Exception error) { entry.Rejection = error; throw; }
        }
        // Every member is admitted before checking cancellation or sending any member.
        // With no members, let the aggregate retain its own empty-operation semantics.
        if (_entries.Length != 0) _token.ThrowIfCancellationRequested();
    }

    internal void FailAcknowledgement(Exception error) => _acknowledgementError = error;

    public void Dispose()
    {
        for (var i = 0; i < _entered; i++)
        {
            ref var entry = ref _entries[i];
            var error = entry.Rejection ?? (i < _operations.Count
                ? _operations[i].IsAborted ? new RespireTransactionAbortedException() : _operations[i].Error
                : _acknowledgementError);
            var outcome = entry.Rejection is not null ? RespireCommandOutcome.Rejected
                : error is OperationCanceledException ? RespireCommandOutcome.Canceled
                : error is not null ? RespireCommandOutcome.Failed : RespireCommandOutcome.Succeeded;
            try { _hook.OnCompleted(entry.Context, entry.State, outcome, error); }
            catch (Exception) { /* Observers cannot replace pending results or interrupt cleanup. */ }
        }
    }
}
