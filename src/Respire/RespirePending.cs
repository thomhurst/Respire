using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Respire;

/// <summary>The lifecycle state of a deferred batch or transaction result.</summary>
public enum RespirePendingStatus
{
    Pending,
    Succeeded,
    Faulted,
    Aborted,
}

/// <summary>
/// The future result of a command queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Readable (or awaitable) only after the batch is executed /
/// the transaction committed — touching it earlier throws immediately instead of deadlocking,
/// while the synchronous queueing method names make an accidental early await conspicuous.
/// </summary>
public sealed class RespirePending<T>
{
    private int _state;
    private T? _value;
    private Exception? _error;

    internal RespirePending()
    {
    }

    /// <summary>The pending result's current lifecycle state.</summary>
    public RespirePendingStatus Status => (RespirePendingStatus)Volatile.Read(ref _state);

    /// <summary>Whether the command has reached a terminal state.</summary>
    public bool IsCompleted => Status != RespirePendingStatus.Pending;

    /// <summary>Whether the command completed successfully and its result is available.</summary>
    public bool HasResult => Status == RespirePendingStatus.Succeeded;

    /// <summary>The command failure when <see cref="Status"/> is <see cref="RespirePendingStatus.Faulted"/>.</summary>
    public Exception? Error => Status == RespirePendingStatus.Faulted ? _error : null;

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
                    ExceptionDispatchInfo.Capture(_error!).Throw();
                    return default!;
                case RespirePendingStatus.Aborted:
                    throw new RespireTransactionAbortedException();
                default:
                    throw new RespirePendingNotReadyException();
            }
        }
    }

    internal void Succeed(T value)
    {
        _value = value;
        Volatile.Write(ref _state, (int)RespirePendingStatus.Succeeded);
    }

    internal void Fail(Exception error)
    {
        _error = error;
        Volatile.Write(ref _state, (int)RespirePendingStatus.Faulted);
    }

    internal void Abort() => Volatile.Write(ref _state, (int)RespirePendingStatus.Aborted);

    public RespirePendingAwaiter<T> GetAwaiter() => new(this);
}

/// <summary>Awaiter for <see cref="RespirePending{T}"/>; completes synchronously.</summary>
public readonly struct RespirePendingAwaiter<T>(RespirePending<T> pending) : ICriticalNotifyCompletion
{
    public bool IsCompleted => true;

    public T GetResult() => pending.Result;

    public void OnCompleted(Action continuation) => continuation();

    public void UnsafeOnCompleted(Action continuation) => continuation();
}
