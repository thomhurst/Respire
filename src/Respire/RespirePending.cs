using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Respire.Internal;

namespace Respire;

/// <summary>The lifecycle state of a deferred batch or transaction result.</summary>
public enum RespirePendingStatus
{
    /// <summary>The containing batch or transaction has not completed.</summary>
    Pending,
    /// <summary>The command produced a result.</summary>
    Succeeded,
    /// <summary>The command or connection failed.</summary>
    Faulted,
    /// <summary>A watched transaction was aborted before commands ran.</summary>
    Aborted,
}

/// <summary>
/// The future result of a command queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Readable (or awaitable) only after the batch is executed /
/// the transaction committed — touching it earlier throws immediately instead of deadlocking,
/// while the synchronous queueing method names make an accidental early await conspicuous.
/// </summary>
public sealed class RespirePending<T> : IDispatchObservation
{
    private const int StatusMask = 3;
    private const int ErrorRecorded = 4;
    private const int NotReadyRecorded = 8;
    private const int AbortRecorded = 16;
    private const int ErrorInspectionAllowed = 32;
    private const int ObservationClosed = 64;
    private int _state;
    private T? _value;
    // Keep the successful pending layout unchanged. A retry replaces this existing error
    // slot with failure-only state; terminal publication restores the original exception.
    private object? _failure;
    private int _errorAttempts;

    internal RespireTelemetry.ErrorObservation Observation => new(this, 0);

    // Read only while holding this pending's gate. Copies passed to transport retain this
    // single-shot owner; exporters run after releasing the gate.
    private int ErrorAttempts => _failure is RetryObservation retry ? retry.Owner.RetryAttempts : _errorAttempts;
    private Exception? Failure => _failure is RetryObservation retry ? retry.Error : (Exception?)_failure;

    private sealed class RetryObservation(ErrorObservation.FinalOwner owner, Exception? error)
    {
        internal readonly ErrorObservation.FinalOwner Owner = owner;
        internal Exception? Error = error;
    }

    int IDispatchObservation.Attempts(long generation)
    {
        lock (this) return ErrorAttempts;
    }

    bool IDispatchObservation.IsOpen(long generation)
        => (Volatile.Read(ref _state) & ObservationClosed) == 0;

    void IDispatchObservation.SetAttempts(long generation, int attempts)
    {
        lock (this)
        {
            if ((Volatile.Read(ref _state) & ObservationClosed) != 0) return;
            _errorAttempts = Math.Max(0, attempts);
            if (_failure is RetryObservation retry) retry.Owner.SetRetryAttempts(_errorAttempts);
        }
    }

    bool IDispatchObservation.Handled(long generation, Exception error)
    {
        ErrorObservation.Borrower borrower;
        lock (this)
        {
            if ((Volatile.Read(ref _state) & ObservationClosed) != 0) return false;
            if (_failure is not RetryObservation)
                _failure = new RetryObservation(ErrorObservation.StartFailure(_errorAttempts), (Exception?)_failure);
            borrower = ((RetryObservation)_failure).Owner.Borrow();
        }
        try { return borrower.RecordHandled(error); }
        finally { borrower.Complete(); }
    }

    internal RespirePending()
    {
    }

    /// <summary>The pending result's current lifecycle state.</summary>
    public RespirePendingStatus Status => (RespirePendingStatus)(Volatile.Read(ref _state) & StatusMask);

    /// <summary>Whether the command has reached a terminal state.</summary>
    public bool IsCompleted => Status != RespirePendingStatus.Pending;

    /// <summary>Whether the command completed successfully and its result is available.</summary>
    public bool HasResult => Status == RespirePendingStatus.Succeeded;

    /// <summary>The command failure when <see cref="Status"/> is <see cref="RespirePendingStatus.Faulted"/>.</summary>
    public Exception? Error => Status == RespirePendingStatus.Faulted ? Failure : null;

    /// <summary>Gets a successful result without throwing; returns false for every other state.</summary>
    public bool TryGetResult([MaybeNullWhen(false)] out T value)
    {
        if (Status == RespirePendingStatus.Succeeded)
        {
            value = _value!;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// The command's result. Throws <see cref="RespirePendingNotReadyException"/> if execution has
    /// not started, or <see cref="RespireTransactionAbortedException"/> if a watched key changed.
    /// </summary>
    public T Result
    {
        get
        {
            switch (Status)
            {
                case RespirePendingStatus.Succeeded:
                    return _value!;
                case RespirePendingStatus.Faulted:
                    if ((Volatile.Read(ref _state) & ErrorInspectionAllowed) != 0) ReportError();
                    ExceptionDispatchInfo.Capture(Failure!).Throw();
                    return default!;
                case RespirePendingStatus.Aborted:
                    throw ObserveInspection(new RespireTransactionAbortedException(), AbortRecorded);
                default:
                    throw ObserveInspection(new RespirePendingNotReadyException(), NotReadyRecorded);
            }
        }
    }

    internal void Succeed(T value)
    {
        _value = value;
        SetStatus(RespirePendingStatus.Succeeded);
    }

    internal void Fail(Exception error)
    {
        lock (this)
        {
            if (_failure is RetryObservation retry) retry.Error = error;
            else _failure = error;
        }
        SetStatus(RespirePendingStatus.Faulted);
    }

    // The batch/transaction owner reports after its correction and resource cleanup.
    // Reading Result again, or creating its summary, must not repeat this boundary.
    internal bool ReportError()
    {
        var faulted = Status == RespirePendingStatus.Faulted;
        if (!faulted && _failure is null)
        {
            Interlocked.Or(ref _state, ObservationClosed);
            return false;
        }
        ErrorObservation.FinalOwner owner;
        int attempts;
        Exception? error;
        lock (this)
        {
            attempts = _errorAttempts = ErrorAttempts;
            var failure = Failure;
            owner = _failure is RetryObservation retry ? retry.Owner : default;
            _failure = failure;
            Interlocked.Or(ref _state, ObservationClosed);
            error = faulted && (Interlocked.Or(ref _state, ErrorRecorded) & ErrorRecorded) == 0 ? failure : null;
        }
        ErrorObservation.FinishFinal(owner, error, retryAttempts: attempts);
        return faulted;
    }

    private TException ObserveInspection<TException>(TException error, int flag) where TException : Exception
    {
        if ((Interlocked.Or(ref _state, flag) & flag) == 0)
            ErrorObservation.FinishFinal(default, error);
        return error;
    }

    internal void AdvanceErrorAttempt() => AddErrorAttempts(1);

    internal void AddErrorAttempts(int attempts)
    {
        lock (this)
        {
            if ((Volatile.Read(ref _state) & ObservationClosed) != 0) return;
            _errorAttempts = ErrorAttempts + attempts;
            if (_failure is RetryObservation retry) retry.Owner.SetRetryAttempts(_errorAttempts);
        }
    }

    internal void Abort() => SetStatus(RespirePendingStatus.Aborted);

    internal void AllowErrorInspection() => Interlocked.Or(ref _state, ErrorInspectionAllowed);

    // The batch owns terminal transitions. Preserve its reported-error flag if it updates
    // the outcome during cleanup; the flag shares existing status storage instead of adding padding.
    private void SetStatus(RespirePendingStatus status)
    {
        int state;
        do { state = Volatile.Read(ref _state); }
        while (Interlocked.CompareExchange(ref _state, (state & ~StatusMask) | (int)status, state) != state);
    }

    /// <summary>Returns the synchronous awaiter for this deferred result.</summary>
    public RespirePendingAwaiter<T> GetAwaiter() => new(this);
}

/// <summary>Awaiter for <see cref="RespirePending{T}"/>; completes synchronously.</summary>
public readonly struct RespirePendingAwaiter<T>(RespirePending<T> pending) : ICriticalNotifyCompletion
{
    /// <summary>Always true; reading before execution throws instead of suspending.</summary>
    public bool IsCompleted => true;

    /// <summary>Returns the deferred result or throws its terminal error.</summary>
    public T GetResult() => pending.Result;

    /// <summary>Runs a continuation synchronously.</summary>
    public void OnCompleted(Action continuation) => continuation();

    /// <summary>Runs a continuation synchronously without execution-context flow.</summary>
    public void UnsafeOnCompleted(Action continuation) => continuation();
}
