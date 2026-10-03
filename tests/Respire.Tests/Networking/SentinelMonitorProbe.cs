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
    internal bool IgnoreCancellation;
    private CancellationTokenRegistration _cancellation;

    public Task<RespireSubscriptionEndReason> Completion => Task.FromResult(RespireSubscriptionEndReason.ClientDisposed);

    public async IAsyncEnumerator<RespireMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        await foreach (var message in Messages.Reader.ReadAllAsync(IgnoreCancellation ? default : cancellationToken))
        {
            yield return message;
            MessageProcessed.TrySetResult();
        }
    }

    internal ISentinelMonitorClient Client => new ClientAdapter(this);

    private sealed class ClientAdapter(SentinelMonitorProbe owner) : ISentinelMonitorClient
    {
        public ValueTask<ISentinelMonitorSubscription> SubscribeAsync(CancellationToken cancellationToken)
        {
            owner._cancellation = cancellationToken.Register(() =>
            {
                owner.Cancelled.TrySetResult();
                owner.CancellationCallback?.Invoke();
            });
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
