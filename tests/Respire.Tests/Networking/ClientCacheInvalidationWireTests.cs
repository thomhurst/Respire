using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCacheInvalidationWireTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] Invalidation = ">2\r\n+invalidate\r\n*1\r\n$10\r\ntenant:key\r\n"u8.ToArray();

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn, false)]
    [Arguments(RespireClientTrackingMode.OptIn, true)]
    [Arguments(RespireClientTrackingMode.Broadcast, false)]
    [Arguments(RespireClientTrackingMode.Broadcast, true)]
    public async Task ObserverCanReadFreshHashFieldsAfterInvalidation(RespireClientTrackingMode mode, bool coalesce)
    {
        var updated = 0;
        await using var server = CreateServer();
        var originalReply = server.ReplyOverride!;
        server.ReplyOverride = (connection, command) => command == "HMGET tenant:key a b"
            ? Volatile.Read(ref updated) == 0
                ? "*2\r\n$3\r\nold\r\n$-1\r\n"u8.ToArray()
                : "*2\r\n$3\r\nnew\r\n$1\r\nB\r\n"u8.ToArray()
            : originalReply(connection, command);
        await using var client = await ConnectAsync(server, mode, coalesce, reuseHashFields: true);
        var view = client.WithKeyPrefix("tenant:");
        var observed = new TaskCompletionSource<string?[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("tenant:key", _ =>
        {
            try
            {
                observed.TrySetResult(view.Hashes.GetManyAsync("key", "a", "b").AsTask()
                    .WaitAsync(Limit).GetAwaiter().GetResult());
            }
            catch (Exception error) { observed.TrySetException(error); }
        });
        await Assert.That(await view.Hashes.GetManyAsync("key", "a", "b"))
            .IsEquivalentTo(new string?[] { "old", null });
        Volatile.Write(ref updated, 1);
        await Assert.That(await view.Hashes.GetManyAsync("key", "a", "b"))
            .IsEquivalentTo(new string?[] { "old", null });
        await server.SendRawAsync(Invalidation);
        await Assert.That(await observed.Task.WaitAsync(Limit))
            .IsEquivalentTo(new string?[] { "new", "B" });
        await Assert.That(server.ReceivedCommands.Count(command => command == "HMGET tenant:key a b"))
            .IsEqualTo(2);
    }

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    public async Task SlowObserverDoesNotBlockEvictionOrSocketReplies(RespireClientTrackingMode mode)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server, mode);
        var view = client.WithKeyPrefix("tenant:");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = view.ClientSideCache!.SubscribeInvalidations("tenant:key", _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            returned.TrySetResult();
        });
        try
        {
            await Assert.That(await view.GetStringAsync("key")).IsEqualTo("value");
            await server.SendRawAsync(Invalidation);
            await entered.Task.WaitAsync(Limit);
            await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
            await client.PingAsync().AsTask().WaitAsync(Limit);
            await Assert.That(await view.GetStringAsync("key")).IsEqualTo("value");
            await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
        }
        finally { release.TrySetResult(); }
        await returned.Task.WaitAsync(Limit);
        await Assert.That(subscription.LastObserverException).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ObserverCanSynchronouslyDisposeSubscriptionAndClient(bool coalesce)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server, RespireClientTrackingMode.OptIn, coalesce);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IRespireClientCacheInvalidationSubscription? subscription = null;
        subscription = client.ClientSideCache!.SubscribeInvalidations("tenant:key", _ =>
        {
            try
            {
                subscription!.Dispose();
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        });
        using (subscription)
        {
            await client.GetStringAsync("tenant:key");
            await server.SendRawAsync(Invalidation);
            await completed.Task.WaitAsync(Limit);
            await Assert.That(subscription.IsDisposed).IsTrue();
            await Assert.That(() => client.ClientSideCache.SubscribeInvalidations("tenant:key", _ => { }))
                .ThrowsExactly<ObjectDisposedException>();
        }
    }

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    public async Task LocalMutationAndConnectionLossWakeAnExistingSubscription(RespireClientTrackingMode mode)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server, mode);
        var mutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.State == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("tenant:key", change =>
        {
            if (change.Reasons.HasFlag(RespireClientCacheInvalidationReason.LocalMutation)) mutation.TrySetResult();
            if (change.Reasons.HasFlag(RespireClientCacheInvalidationReason.ContinuityLost)) lost.TrySetResult();
            if (change.Reasons.HasFlag(RespireClientCacheInvalidationReason.ServerInvalidation)) afterReconnect.TrySetResult();
        });
        await client.GetStringAsync("tenant:key");
        await client.SetAsync("tenant:key", "new");
        await mutation.Task.WaitAsync(Limit);
        await client.GetStringAsync("tenant:key");
        server.CloseConnectionAfterCommand = server.CommandsSeen + 1;
        try { await client.PingAsync().AsTask().WaitAsync(Limit); }
        catch (RespireException) { }
        await lost.Task.WaitAsync(Limit);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        // Ordinary sends can fail fast while scheduling replacement. Wait for its actual
        // publication before reading, rather than racing the background recovery task.
        try { await client.PingAsync().AsTask().WaitAsync(Limit); }
        catch (RespireConnectionException) { }
        await recovered.Task.WaitAsync(Limit);
        await Assert.That(await client.GetStringAsync("tenant:key").AsTask().WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("CLIENT TRACKING ON")))
            .IsEqualTo(2);
        await server.SendRawAsync(Invalidation, connectionId: 1);
        await afterReconnect.Task.WaitAsync(Limit);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
    }

    private static FakeRespServer CreateServer() => new(2, FakeRespServer.OkReply)
    {
        ReplyOverride = static (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET tenant:key" => "$5\r\nvalue\r\n"u8.ToArray(),
            "PING" => FakeRespServer.PongReply,
            _ => FakeRespServer.OkReply,
        },
    };

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server, RespireClientTrackingMode mode,
        bool coalesce = false, bool reuseHashFields = false)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            ClientSideCache = new()
            {
                CoalesceConcurrentMisses = coalesce,
                ReuseHashFields = reuseHashFields,
                TrackingMode = mode,
                BroadcastPrefixes = mode == RespireClientTrackingMode.Broadcast ? ["tenant:"] : [],
            },
        });
}
