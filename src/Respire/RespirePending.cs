using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Respire;

/// <summary>
/// The future result of a command queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Readable (or awaitable) only after the batch is sent /
/// the transaction committed — touching it earlier throws immediately instead of deadlocking,
/// which makes the classic await-before-flush bug impossible.
/// </summary>
public sealed class RespirePending<T>
{
    private const int StatePending = 0;
    private const int StateSucceeded = 1;
    private const int StateFaulted = 2;
    private const int StateAborted = 3;

    private int _state;
    private T? _value;
    private Exception? _error;

    internal RespirePending()
    {
    }

    public bool IsCompleted => Volatile.Read(ref _state) != StatePending;

    /// <summary>
    /// The command's result. Throws <see cref="InvalidOperationException"/> if the batch has not
    /// been sent, or if the transaction was aborted because a watched key changed.
    /// </summary>
    public T Result
    {
        get
        {
            switch (Volatile.Read(ref _state))
            {
                case StateSucceeded:
                    return _value!;
                case StateFaulted:
                    ExceptionDispatchInfo.Capture(_error!).Throw();
                    return default!;
                case StateAborted:
                    throw new InvalidOperationException(
                        "The transaction was aborted — a watched key changed, so no command ran.");
                default:
                    throw new InvalidOperationException(
                        "This result is not available yet: send the batch (SendAsync) or commit the transaction (CommitAsync) first.");
            }
        }
    }

    internal void Succeed(T value)
    {
        _value = value;
        Volatile.Write(ref _state, StateSucceeded);
    }

    internal void Fail(Exception error)
    {
        _error = error;
        Volatile.Write(ref _state, StateFaulted);
    }

    internal void Abort() => Volatile.Write(ref _state, StateAborted);

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
