using Respire.Networking;

namespace Respire.Internal;

/// <summary>Arms an upload deadline only while discovering or acquiring its connection.</summary>
internal sealed class DedicatedAcquisitionCancellation : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _source;
    private readonly Timer _timer;
    private CommandDeadline _deadline;
    private bool _disposed;

    internal DedicatedAcquisitionCancellation(CancellationToken callerToken)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _timer = new Timer(static state => ((DedicatedAcquisitionCancellation)state!).Schedule(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal CancellationToken Token => _source.Token;
    internal bool IsCancellationRequested => _source.IsCancellationRequested;

    internal void Arm(CommandDeadline deadline)
    {
        lock (_gate)
        {
            _deadline = deadline;
            Schedule();
        }
    }

    internal void Disarm() => Arm(CommandDeadline.None);

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_deadline.IsSet)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }
            var remaining = _deadline.Ticks - Environment.TickCount64;
            if (remaining > 0)
            {
                // CommandTimeout can exceed System.Threading.Timer's approximately 49-day limit.
                _timer.Change(TimeSpan.FromMilliseconds(Math.Min(remaining, int.MaxValue)), Timeout.InfiniteTimeSpan);
                return;
            }
            // Publish cancellation under the gate; callbacks must not run under that gate.
            var callbacks = _source.CancelAsync();
            _ = callbacks.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            _source.Dispose();
        }
    }
}
