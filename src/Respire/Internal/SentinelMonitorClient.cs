namespace Respire.Internal;

// Transport seam for shutdown tests, including resources that ignore cancellation.
internal interface ISentinelMonitorClient : IAsyncDisposable
{
    ValueTask<ISentinelMonitorSubscription> SubscribeAsync(CancellationToken cancellationToken);
}

internal interface ISentinelMonitorSubscription : IAsyncDisposable, IAsyncEnumerable<RespireMessage>
{
    Task<RespireSubscriptionEndReason> Completion { get; }
}

internal sealed class SentinelMonitorClient(RespireOptions options) : ISentinelMonitorClient
{
    private readonly RespireClient _client = RespireClient.Create(options);

    public async ValueTask<ISentinelMonitorSubscription> SubscribeAsync(CancellationToken cancellationToken)
        => new Subscription(await _client.SubscribeAsync(
            ["+switch-master", "+sdown", "+odown"], cancellationToken).ConfigureAwait(false));

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private sealed class Subscription(RespireSubscription subscription) : ISentinelMonitorSubscription
    {
        public Task<RespireSubscriptionEndReason> Completion => subscription.Completion;
        public IAsyncEnumerator<RespireMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => subscription.GetAsyncEnumerator(cancellationToken);
        public ValueTask DisposeAsync() => subscription.DisposeAsync();
    }
}
