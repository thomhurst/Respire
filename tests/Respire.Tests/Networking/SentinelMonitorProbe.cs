using System.Threading.Channels;
using Respire.Internal;

namespace Respire.Tests.Networking;

internal sealed class SentinelMonitorProbe
{
    internal readonly Channel<RespireMessage> Messages = Channel.CreateUnbounded<RespireMessage>();
    internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource ClientCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource SubscriptionCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource MessageProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<ValueTask> DisposeClient = static () => ValueTask.CompletedTask;
    internal Func<ValueTask> DisposeSubscription = static () => ValueTask.CompletedTask;
    internal Action? CancellationCallback;
    internal Action? Subscribed;
    internal event Action<RespireConnectionStateChange>? ConnectionStateChanged;
    internal void ChangeConnectionState(RespireConnectionStateChange change) => ConnectionStateChanged?.Invoke(change);
    internal bool IgnoreCancellation;
    internal CancellationToken SubscriptionToken;
    private CancellationTokenRegistration _cancellation;

    public Task<RespireSubscriptionEndReason> Completion => Task.FromResult(RespireSubscriptionEndReason.ClientDisposed);

    public async IAsyncEnumerator<RespireMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var message in Messages.Reader.ReadAllAsync(IgnoreCancellation ? default : cancellationToken))
            {
                yield return message;
                MessageProcessed.TrySetResult();
            }
        }
        finally
        {
            // A cancelled read can finish and dispose the subscription registration
            // before that registration's callback runs. Report observed cancellation
            // here too, without changing probes that intentionally ignore the token.
            if (!IgnoreCancellation && cancellationToken.IsCancellationRequested)
                Cancelled.TrySetResult();
        }
    }

    internal ISentinelMonitorClient Client => new ClientAdapter(this);

    private sealed class ClientAdapter(SentinelMonitorProbe owner) : ISentinelMonitorClient
    {
        public event Action<RespireConnectionStateChange>? ConnectionStateChanged
        {
            add => owner.ConnectionStateChanged += value;
            remove => owner.ConnectionStateChanged -= value;
        }
        public ValueTask<ISentinelMonitorSubscription> SubscribeAsync(CancellationToken cancellationToken)
        {
            owner.SubscriptionToken = cancellationToken;
            owner._cancellation = cancellationToken.Register(() =>
            {
                owner.Cancelled.TrySetResult();
                owner.CancellationCallback?.Invoke();
            });
            owner.Subscribed?.Invoke();
            return ValueTask.FromResult<ISentinelMonitorSubscription>(new SubscriptionAdapter(owner));
        }
        public async ValueTask DisposeAsync()
        {
            owner.ClientCleanup.TrySetResult();
            await owner.DisposeClient();
        }
    }

    private sealed class SubscriptionAdapter(SentinelMonitorProbe owner) : ISentinelMonitorSubscription
    {
        public Task<RespireSubscriptionEndReason> Completion => owner.Completion;
        public IAsyncEnumerator<RespireMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => owner.GetAsyncEnumerator(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            owner.SubscriptionCleanup.TrySetResult();
            try { await owner.DisposeSubscription(); }
            finally { owner._cancellation.Dispose(); }
        }
    }
}
