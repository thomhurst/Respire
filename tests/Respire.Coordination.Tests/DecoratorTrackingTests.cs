using System.Reflection;
using System.Runtime.ExceptionServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class DecoratorTrackingTests
{
    [ClassDataSource<SharedRedis74>(Shared = SharedType.PerTestSession)]
    public required SharedRedis74 Redis { get; init; }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlyReleaseInvalidatesDecoratedRegistrationDuringMutation(bool hashField)
    {
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Redis.CreateOptions() with
        {
            Protocol = RespireProtocol.Resp3, Connections = 1,
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        };
        await using var ownerClient = await RespireClient.ConnectAsync(options);
        await using var waiterClient = await RespireClient.ConnectAsync(options);
        var prefix = SharedRespireContainer.Prefix("decorator");
        var owner = new RespireCoordination(ownerClient.WithKeyPrefix(prefix));
        var proxy = DispatchProxy.Create<IRespireClient, ForwardingClient>();
        ((ForwardingClient)proxy).Inner = waiterClient.WithKeyPrefix(prefix);
        // Registration supports forwarding decorators, but coordination wait admission
        // deliberately requires a built-in client whose routing can be verified.
        var waiter = new RespireCoordination(waiterClient.WithKeyPrefix(prefix));
        var duration = TimeSpan.FromSeconds(30);
        await using IAsyncDisposable holder = hashField
            ? await owner.TryAcquireLeaseAsync("lease", "worker", duration)
                ?? throw new InvalidOperationException("Expected a lease.")
            : (await owner.TryAcquireFencedLockAsync("lease", "counter", duration)).Lock;
        var cache = (ClientSideCacheCoordinator)waiterClient.ClientSideCache!;
        var mutation = cache.BeginUnknownMutation();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var subscription = cache.SubscribeInvalidations(prefix + "lease", notification =>
        {
            if ((notification.Reasons & RespireClientCacheInvalidationReason.ServerInvalidation) != 0)
                invalidated.TrySetResult();
        }, cancellation.Token);
        try
        {
            await CoordinationCommandHooks.TrackAsync(proxy, "lease",
                hashField ? (RespireKey)"worker" : (RespireKey?)null, cancellation.Token);
            await Assert.That(cache.Count).IsEqualTo(0);
            await Assert.That(invalidated.Task.IsCompleted).IsFalse();
            await holder.DisposeAsync();
            await invalidated.Task.WaitAsync(cancellation.Token);
            await using var acquired = await AcquireAsync(waiter, hashField, duration, cancellation.Token);
            await Assert.That(cache.Count).IsEqualTo(0);
        }
        finally
        {
            cache.CompleteMutation(in mutation, succeeded: false);
        }
    }

    private static async Task<IAsyncDisposable> AcquireAsync(
        RespireCoordination coordination, bool hashField, TimeSpan duration, CancellationToken token)
        => hashField
            ? await coordination.AcquireLeaseAsync("lease", "worker", duration, token)
            : await coordination.AcquireFencedLockAsync("lease", "counter", duration, token);

    public class ForwardingClient : DispatchProxy, IRespireNameResolver
    {
        public IRespireClient Inner { get; set; } = null!;
        public Action<MethodInfo, object?[]>? Before { get; set; }

        public RespireKey ResolveKey(RespireKey key) => ((IRespireNameResolver)Inner).ResolveKey(key);
        public RespireChannel ResolveChannel(RespireChannel channel) => ((IRespireNameResolver)Inner).ResolveChannel(channel);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Before?.Invoke(targetMethod!, args!);
            try { return targetMethod!.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }
}
