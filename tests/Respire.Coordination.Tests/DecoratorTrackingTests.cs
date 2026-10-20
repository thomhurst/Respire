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
    public async Task EarlyReleaseWakesDecoratedWaiterDuringMutation(bool hashField)
    {
        var expiryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new ExpiryHook(hashField ? "HPTTL" : "PTTL", expiryRead);
        var options = Redis.CreateOptions() with
        {
            Protocol = RespireProtocol.Resp3, Connections = 1,
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        };
        await using var ownerClient = await RespireClient.ConnectAsync(options);
        await using var waiterClient = await RespireClient.ConnectAsync(options with { CommandHook = hook });
        var prefix = SharedRespireContainer.Prefix("decorator");
        var owner = new RespireCoordination(ownerClient.WithKeyPrefix(prefix));
        var proxy = DispatchProxy.Create<IRespireClient, ForwardingClient>();
        ((ForwardingClient)proxy).Inner = waiterClient.WithKeyPrefix(prefix);
        var waiter = new RespireCoordination(proxy);
        var duration = TimeSpan.FromSeconds(30);
        await using IAsyncDisposable holder = hashField
            ? await owner.TryAcquireLeaseAsync("lease", "worker", duration)
                ?? throw new InvalidOperationException("Expected a lease.")
            : (await owner.TryAcquireFencedLockAsync("lease", "counter", duration)).Lock;
        var cache = (ClientSideCacheCoordinator)waiterClient.ClientSideCache!;
        var mutation = cache.BeginUnknownMutation();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<IAsyncDisposable>? pending = null;
        try
        {
            pending = AcquireAsync(waiter, hashField, duration, cancellation.Token);
            await expiryRead.Task.WaitAsync(cancellation.Token);
            await Assert.That(pending.IsCompleted).IsFalse();
            await holder.DisposeAsync();
            await using var acquired = await pending;
            pending = null;
            await Assert.That(cache.Count).IsEqualTo(0);
        }
        finally
        {
            try
            {
                await cancellation.CancelAsync();
                if (pending is not null)
                {
                    try { await using var acquired = await pending; }
                    catch (OperationCanceledException) { }
                }
            }
            finally { cache.CompleteMutation(in mutation, succeeded: false); }
        }
    }

    private static async Task<IAsyncDisposable> AcquireAsync(
        RespireCoordination coordination, bool hashField, TimeSpan duration, CancellationToken token)
        => hashField
            ? await coordination.AcquireLeaseAsync("lease", "worker", duration, token)
            : await coordination.AcquireFencedLockAsync("lease", "counter", duration, token);

    private sealed class ExpiryHook(string command, TaskCompletionSource completed) : RespireCommandHook
    {
        public override object? OnExecuting(RespireCommandContext context) => null;

        public override void OnCompleted(RespireCommandContext context, object? state,
            RespireCommandOutcome outcome, Exception? error)
        {
            if (context.Command == command && outcome == RespireCommandOutcome.Succeeded)
                completed.TrySetResult();
        }
    }

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
