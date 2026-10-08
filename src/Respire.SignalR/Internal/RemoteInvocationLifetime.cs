using Microsoft.AspNetCore.SignalR.Protocol;

namespace Respire.SignalR.Internal;

/// <summary>Bounds a receiver's forwarded result state without changing Microsoft's wire protocol.</summary>
internal sealed class RemoteInvocationLifetime
{
    private readonly Lock _gate = new();
    private readonly ClientResultsManager _results;
    private readonly string _invocationId;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly CancellationToken _disconnected;
    private readonly Func<CompletionMessage, Task> _forward;
    private readonly TimeSpan _timeout;
    private CancellationTokenRegistration _registration;
    private bool _completed;

    internal RemoteInvocationLifetime(ClientResultsManager results, string invocationId,
        CancellationToken disconnected, TimeSpan timeout, Func<CompletionMessage, Task> forward)
    {
        _results = results;
        _invocationId = invocationId;
        _disconnected = disconnected;
        _forward = forward;
        _timeout = timeout;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        _token = _lifetime.Token;
    }

    // Call only after the invocation is in the index. An already-canceled token
    // can complete synchronously before UnsafeRegister returns its registration.
    internal void Start()
    {
        lock (_gate)
        {
            if (_completed) return;
            _lifetime.CancelAfter(_timeout);
        }
        var registration = _token.UnsafeRegister(static state => ((RemoteInvocationLifetime)state!).Expire(), this);
        lock (_gate)
        {
            if (!_completed)
            {
                _registration = registration;
                return;
            }
        }
        registration.Dispose();
    }

    // An unsuccessful index registration has no callbacks or timer yet.
    internal void DisposeBeforeStart() => _lifetime.Dispose();

    internal async Task CompleteAsync(CompletionMessage message)
    {
        CancellationTokenRegistration registration;
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            registration = _registration;
        }
        _lifetime.Dispose();
        // Async disposal also works when completion runs inside this registration.
        await registration.DisposeAsync().ConfigureAwait(false);
        await _forward(message).ConfigureAwait(false);
    }

    private void Expire()
    {
        if (_results.RemoveInvocation(_invocationId) is { } pending)
        {
            var error = _disconnected.IsCancellationRequested
                ? "Connection disconnected." : "Remote client result timed out.";
            // Forwarding catches transport failures; pub/sub cannot replay a lost completion.
            _ = pending.Completion(pending.Tcs, CompletionMessage.WithError(_invocationId, error));
        }
    }
}
