using System.Text;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientSideCacheCoalescingTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EquivalentMissesSendOneRequestAndCancellationOnlyDetachesCaller(bool cancelLeader)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        using var canceled = new CancellationTokenSource();
        var leader = client.GetStringAsync("key", cancelLeader ? canceled.Token : default).AsTask();
        var follower = client.GetStringAsync("key", cancelLeader ? default : canceled.Token).AsTask();
        var third = client.GetStringAsync("key").AsTask();
        await BarrierAsync(server, client, 1);
        canceled.Cancel();
        await Assert.That(async () => await (cancelLeader ? leader : follower)).Throws<OperationCanceledException>();
        await Assert.That((cancelLeader ? follower : leader).IsCompleted).IsFalse();
        await server.SendRawAsync("+OK\r\n$5\r\nvalue\r\n+PONG\r\n"u8.ToArray());
        await Assert.That(await (cancelLeader ? follower : leader).WaitAsync(Timeout)).IsEqualTo("value");
        await Assert.That(await third.WaitAsync(Timeout)).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedAndRawGetShareTheSameWireRead(bool rawFirst)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        Task<RespireResult>? raw = rawFirst ? client.ExecuteAsync("GET", "key").AsTask() : null;
        var typed = client.GetStringAsync("key").AsTask();
        raw ??= client.ExecuteAsync("GET", "key").AsTask();
        await BarrierAsync(server, client, 1);
        await server.SendRawAsync("+OK\r\n$5\r\nvalue\r\n+PONG\r\n"u8.ToArray());
        using var result = await raw.WaitAsync(Timeout);
        await Assert.That(result.AsString()).IsEqualTo(await typed.WaitAsync(Timeout));
        await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(Timeout)).IsEqualTo("value");
        using var cached = await client.ExecuteAsync("GET", "key").AsTask().WaitAsync(Timeout);
        await Assert.That(cached.AsString()).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedAndRawMGetSharePerKeyEntriesAndResolvedPrefixes(bool rawFirst)
    {
        await using var server = CreateServer();
        await using var root = await ConnectAsync(server);
        var client = root.WithKeyPrefix("tenant:");
        // Raw execution takes physical wire arguments; prefix views transform typed keys only.
        Task<RespireResult>? raw = rawFirst ? client.ExecuteAsync("MGET", "tenant:a", "tenant:b", "tenant:a").AsTask() : null;
        var typed = client.Strings.GetManyAsync("a", "b", "a").AsTask();
        raw ??= client.ExecuteAsync("MGET", "tenant:a", "tenant:b", "tenant:a").AsTask();
        await BarrierAsync(server, root, 1);
        await server.SendRawAsync("+OK\r\n*3\r\n$1\r\nx\r\n$-1\r\n$1\r\nx\r\n+PONG\r\n"u8.ToArray());
        using var result = await raw.WaitAsync(Timeout);
        await Assert.That(await typed.WaitAsync(Timeout)).IsEquivalentTo(new string?[] { "x", null, "x" });
        await Assert.That(result[0].AsString()).IsEqualTo("x");
        await Assert.That(result[1].IsNull).IsTrue();
        await Assert.That(await client.Strings.GetManyAsync("a", "b", "a").AsTask().WaitAsync(Timeout))
            .IsEquivalentTo(new string?[] { "x", null, "x" });
        using var cached = await client.ExecuteAsync("MGET", "tenant:a", "tenant:b", "tenant:a").AsTask().WaitAsync(Timeout);
        await Assert.That(cached[2].AsString()).IsEqualTo("x");
        await Assert.That(cached[1].IsNull).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("MGET "))).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).Contains("MGET tenant:a tenant:b tenant:a");
    }

    [Test]
    public async Task AggregateWaitersOwnIndependentReplies()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        var first = client.ExecuteAsync("HGETALL", "hash").AsTask();
        var second = client.ExecuteAsync("HGETALL", "hash").AsTask();
        await BarrierAsync(server, client, 1);
        await server.SendRawAsync("+OK\r\n*2\r\n$1\r\na\r\n$1\r\nb\r\n+PONG\r\n"u8.ToArray());
        using var left = await first.WaitAsync(Timeout);
        using var right = await second.WaitAsync(Timeout);
        // Mutate the actual owned payload, not a conversion copy. Each waiter and the
        // resident cache must remain independent even when the last waiter takes ownership.
        System.Runtime.InteropServices.MemoryMarshal.GetReference(left[1].AsSpan()) = (byte)'z';
        left.Dispose();
        await Assert.That(right[1].AsString()).IsEqualTo("b");
        using var cached = await client.ExecuteAsync("HGETALL", "hash");
        await Assert.That(cached[1].AsString()).IsEqualTo("b");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BroadcastCoalescingSharesMissesButCachesOnlyTrackedPrefixes(bool covered)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : FakeRespServer.OkReply,
            SuppressReply = command => command.StartsWith("GET "),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            ClientSideCache = new()
            {
                CoalesceConcurrentMisses = true, TrackingMode = RespireClientTrackingMode.Broadcast,
                KeyPrefixes = ["covered:"],
            },
        });
        var key = covered ? "covered:key" : "outside:key";
        var first = client.GetStringAsync(key).AsTask();
        var second = client.GetStringAsync(key).AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Contains("GET " + key));
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(1);
        await server.SendRawAsync("$1\r\nx\r\n"u8.ToArray());
        await Assert.That(await first.WaitAsync(Timeout)).IsEqualTo("x");
        await Assert.That(await second.WaitAsync(Timeout)).IsEqualTo("x");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(covered ? 1 : 0);
        var next = client.GetStringAsync(key).AsTask();
        if (!covered)
        {
            await WaitUntilAsync(() => server.ReceivedCommands.Count(command => command.StartsWith("GET ")) == 2);
            await server.SendRawAsync("$1\r\nx\r\n"u8.ToArray());
        }
        await Assert.That(await next.WaitAsync(Timeout)).IsEqualTo("x");
        await Assert.That(server.ReceivedCommands.Any(command => command == "CLIENT CACHING YES")).IsFalse();
    }

    [Test]
    public async Task ExactMGetMissListsShareOneRequestAndKeepCallerArraysIndependent()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        var first = client.Strings.GetManyAsync("a", "b", "a").AsTask();
        var second = client.Strings.GetManyAsync("a", "b", "a").AsTask();
        await BarrierAsync(server, client, 1);
        await server.SendRawAsync("+OK\r\n*3\r\n$1\r\nx\r\n$-1\r\n$1\r\nx\r\n+PONG\r\n"u8.ToArray());
        var left = await first.WaitAsync(Timeout);
        var right = await second.WaitAsync(Timeout);
        left[0] = "changed";
        await Assert.That(right).IsEquivalentTo(new string?[] { "x", null, "x" });
        await Assert.That(server.ReceivedCommands.Contains("MGET a b a")).IsTrue();
    }

    [Test]
    public async Task FailureReachesEveryCallerAndNextReadRetries()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        var first = client.GetStringAsync("key").AsTask();
        var second = client.GetStringAsync("key").AsTask();
        await BarrierAsync(server, client, 1);
        await server.SendRawAsync("+OK\r\n-ERR failed\r\n+PONG\r\n"u8.ToArray());
        await Assert.That(async () => await first.WaitAsync(Timeout)).Throws<RespireServerException>();
        await Assert.That(async () => await second.WaitAsync(Timeout)).Throws<RespireServerException>();
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
        var retry = client.GetStringAsync("key").AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Count(c => c == "GET key") == 2);
        await server.SendRawAsync("+OK\r\n$3\r\nnew\r\n"u8.ToArray());
        await Assert.That(await retry.WaitAsync(Timeout)).IsEqualTo("new");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidationRetiresJoiningAndRejectsOldInsertion(bool continuityLoss)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        var first = client.GetStringAsync("key").AsTask();
        var joined = client.GetStringAsync("key").AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Contains("GET key"));
        var cache = client.Core.ClientCache!;
        if (continuityLoss) cache.FlushForContinuityLoss();
        else
        {
            await server.SendRawAsync(">2\r\n+invalidate\r\n*1\r\n$3\r\nkey\r\n"u8.ToArray());
            await WaitUntilAsync(() => cache.GetStatistics().Invalidations == 1);
        }
        var fresh = client.GetStringAsync("key").AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Count(c => c == "GET key") == 2);
        await server.SendRawAsync("+OK\r\n$3\r\nold\r\n"u8.ToArray());
        await Assert.That(await first.WaitAsync(Timeout)).IsEqualTo("old");
        await Assert.That(await joined.WaitAsync(Timeout)).IsEqualTo("old");
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(fresh.IsCompleted).IsFalse();
        await server.SendRawAsync("+OK\r\n$3\r\nnew\r\n"u8.ToArray());
        await Assert.That(await fresh.WaitAsync(Timeout)).IsEqualTo("new");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    public async Task LastCancellationRetiresFlightAndAcceptedReplyStillDrainsInOrder()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        using var cancellation = new CancellationTokenSource();
        var first = client.GetStringAsync("key", cancellation.Token).AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Contains("GET key"));
        cancellation.Cancel();
        await Assert.That(async () => await first).Throws<OperationCanceledException>();
        await WaitUntilAsync(() => client.Core.ClientCache!.ActiveSharedReadCount == 0);
        var retry = client.GetStringAsync("key").AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Count(c => c == "GET key") == 2);
        await server.SendRawAsync("+OK\r\n$3\r\nold\r\n+OK\r\n$3\r\nnew\r\n"u8.ToArray());
        await Assert.That(await retry.WaitAsync(Timeout)).IsEqualTo("new");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task CallersDuringEntryRemovalCannotJoinOrPublishStaleSharedWork()
    {
        var cache = new ClientSideCacheCoordinator(new() { CoalesceConcurrentMisses = true });
        RespireKey key = "key";
        var token = cache.BeginRead(in key);
        using var cached = RespValue.BulkString("cached"u8.ToArray());
        cache.CompleteRead(in token, in cached, allowInsert: true);
        var identity = new ClientCacheCommandKey("GET", "key");
        var finishReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async ValueTask<RespValue> Read(int _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await finishReads.Task.WaitAsync(cancellationToken);
            return RespValue.BulkString("value"u8.ToArray());
        }

        // Remove first evicts the scalar entry, then takes the dependency gate. Hold that
        // gate to expose the exact interval between eviction and invalidation completion.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var store = typeof(ClientSideCacheCoordinator).GetField("_store", flags)!.GetValue(cache)!;
        var dependencies = (Lock)store.GetType().GetField("_dependencyLock", flags)!.GetValue(store)!;
        var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            lock (dependencies)
            {
                gateHeld.TrySetResult();
                releaseGate.Task.GetAwaiter().GetResult();
            }
        });
        var reads = new List<Task<RespValue>>();
        Task invalidating = Task.CompletedTask;
        try
        {
            await gateHeld.Task.WaitAsync(Timeout);
            reads.Add(cache.CoalesceReadAsync(identity, 0, Read, default).AsTask());
            invalidating = Task.Run(() => cache.Invalidate(in key));
            await WaitUntilAsync(() => cache.Count == 0);
            reads.Add(cache.CoalesceReadAsync(identity, 0, Read, default).AsTask());
            reads.Add(cache.CoalesceReadAsync(identity, 0, Read, default).AsTask());
            releaseGate.TrySetResult();
            await invalidating.WaitAsync(Timeout);
            reads.Add(cache.CoalesceReadAsync(identity, 0, Read, default).AsTask());
            await Assert.That(calls).IsEqualTo(4);
        }
        finally
        {
            releaseGate.TrySetResult();
            finishReads.TrySetResult();
            await Task.WhenAll(holder, invalidating).WaitAsync(Timeout);
            foreach (var read in reads) (await read.WaitAsync(Timeout)).Dispose();
        }
        await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    public async Task DisposalCancelsBothCurrentAndRetiredFlights()
    {
        await using var server = CreateServer();
        var client = await ConnectAsync(server);
        await using (client)
        {
            var first = client.GetStringAsync("key").AsTask();
            await WaitUntilAsync(() => server.ReceivedCommands.Contains("GET key"));
            client.ClientSideCache!.Clear();
            var second = client.GetStringAsync("key").AsTask();
            await WaitUntilAsync(() => server.ReceivedCommands.Count(c => c == "GET key") == 2);
            await client.DisposeAsync();
            await Assert.That(async () => await first.WaitAsync(Timeout)).Throws<OperationCanceledException>();
            await Assert.That(async () => await second.WaitAsync(Timeout)).Throws<OperationCanceledException>();
            await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task BinaryKeysAndQueryArgumentsRemainDistinctAndSnapshotCallerMemory()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        var bytes = new byte[] { 0, 255, 1 };
        var first = client.Strings.GetRangeAsync(new RespireKey(bytes), 0, 1).AsTask();
        var joined = client.Strings.GetRangeAsync(new RespireKey(new byte[] { 0, 255, 1 }), 0, 1).AsTask();
        bytes[2] = 2;
        var differentKey = client.Strings.GetRangeAsync(new RespireKey(bytes), 0, 1).AsTask();
        var differentRange = client.Strings.GetRangeAsync(new RespireKey(bytes), 1, 2).AsTask();
        await BarrierAsync(server, client, 3);
        await server.SendRawAsync("+OK\r\n$1\r\na\r\n+OK\r\n$1\r\nb\r\n+OK\r\n$1\r\nc\r\n+PONG\r\n"u8.ToArray());
        await Assert.That(await first.WaitAsync(Timeout)).IsEqualTo("a");
        await Assert.That(await joined.WaitAsync(Timeout)).IsEqualTo("a");
        await Assert.That(await differentKey.WaitAsync(Timeout)).IsEqualTo("b");
        await Assert.That(await differentRange.WaitAsync(Timeout)).IsEqualTo("c");
        var frames = server.ReceivedArguments.Where(a => Encoding.ASCII.GetString(a[0]) == "GETRANGE").ToArray();
        await Assert.That(frames[0][1]).IsEquivalentTo(new byte[] { 0, 255, 1 });
        await Assert.That(frames[1][1]).IsEquivalentTo(new byte[] { 0, 255, 2 });
    }

    [Test]
    public async Task CompletedUncacheableReadRetiresAndDoesNotBecomeAnUnboundedCache()
    {
        var cache = new ClientSideCacheCoordinator(new() { CoalesceConcurrentMisses = true });
        var identity = new ClientCacheCommandKey("GET", "key");
        var calls = 0;
        ValueTask<RespValue> Read(int _, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(RespValue.BulkString("oversized"u8.ToArray()));
        }
        using var first = await cache.CoalesceReadAsync(identity, 0, Read, default);
        using var second = await cache.CoalesceReadAsync(identity, 0, Read, default);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    public async Task TransportFailureFansOutWithoutReplayAndReconnectStartsNewTrackedRead()
    {
        await using var server = new FakeRespServer(2, Hello, FakeRespServer.OkReply)
        {
            CloseConnectionAfterCommand = 5,
            SuppressReply = command => !command.StartsWith("HELLO ") && !command.StartsWith("CLIENT TRACKING "),
            ReplyOverride = (_, command) => command.StartsWith("HELLO ") ? Hello
                : command == "GET key" ? "$3\r\nnew\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
        await using var client = await ConnectAsync(server);
        var first = client.GetStringAsync("key").AsTask();
        var second = client.GetStringAsync("key").AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Contains("GET key"));
        var trigger = client.PingAsync().AsTask();
        await Assert.That(async () => await first.WaitAsync(Timeout)).Throws<RespireConnectionException>();
        await Assert.That(async () => await second.WaitAsync(Timeout)).Throws<RespireConnectionException>();
        await Assert.That(async () => await trigger.WaitAsync(Timeout)).Throws<RespireConnectionException>();
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(1);
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        server.SuppressReply = null;
        // Standalone reads fail fast until background replacement completes. A failed
        // accepted read does not imply that the next connection is already published.
        await WaitUntilAsync(() => client.IsConnected
            && server.ReceivedCommands.Count(c => c == "CLIENT TRACKING ON OPTIN") == 2);
        await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(Timeout)).IsEqualTo("new");
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(c => c == "CLIENT TRACKING ON OPTIN")).IsEqualTo(2);
    }

    [Test]
    public async Task PrefixViewsShareResolvedKeysAndKeepDifferentPrefixesSeparate()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        var first = client.WithKeyPrefix("a:").GetStringAsync("key").AsTask();
        var same = client.GetStringAsync("a:key").AsTask();
        var other = client.WithKeyPrefix("b:").GetStringAsync("key").AsTask();
        await BarrierAsync(server, client, 2);
        await server.SendRawAsync("+OK\r\n$1\r\na\r\n+OK\r\n$1\r\nb\r\n+PONG\r\n"u8.ToArray());
        await Assert.That(await first.WaitAsync(Timeout)).IsEqualTo("a");
        await Assert.That(await same.WaitAsync(Timeout)).IsEqualTo("a");
        await Assert.That(await other.WaitAsync(Timeout)).IsEqualTo("b");
    }

    [Test]
    public async Task SeparateClientsAndDatabasesNeverShareWork()
    {
        await using var server = new FakeRespServer(2, Hello, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ") ? Hello : FakeRespServer.OkReply,
            SuppressReply = command => command == "CLIENT CACHING YES" || command.StartsWith("GET "),
        };
        await using var firstClient = await ConnectAsync(server);
        await using var secondClient = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            Database = 1,
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        var first = firstClient.GetStringAsync("key").AsTask();
        var second = secondClient.GetStringAsync("key").AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Count(c => c == "GET key") == 2);
        await server.SendRawAsync("+OK\r\n$1\r\na\r\n"u8.ToArray(), 0);
        await server.SendRawAsync("+OK\r\n$1\r\nb\r\n"u8.ToArray(), 1);
        await Assert.That(await first.WaitAsync(Timeout)).IsEqualTo("a");
        await Assert.That(await second.WaitAsync(Timeout)).IsEqualTo("b");
    }

    [Test]
    [Arguments(true, 1, false)]
    [Arguments(false, 2, false)]
    [Arguments(true, 1, true)]
    [Arguments(false, 2, true)]
    public async Task CoalescingIsExplicitlyEnabled(bool enabled, int requests, bool many)
    {
        await using var server = CreateServer();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            ClientSideCache = enabled ? new() { CoalesceConcurrentMisses = true } : new(),
        });
        async Task<string?> ReadAsync() => many
            ? string.Join(",", await client.Strings.GetManyAsync("key", "missing", "key"))
            : await client.GetStringAsync("key");
        var first = ReadAsync();
        var second = ReadAsync();
        await BarrierAsync(server, client, requests);
        var reply = many ? "*3\r\n$1\r\nx\r\n$-1\r\n$1\r\nx\r\n" : "$1\r\nx\r\n";
        var frames = string.Concat(Enumerable.Repeat("+OK\r\n" + reply, requests)) + "+PONG\r\n";
        await server.SendRawAsync(Encoding.ASCII.GetBytes(frames));
        await Assert.That(await first.WaitAsync(Timeout)).IsEqualTo(many ? "x,,x" : "x");
        await Assert.That(await second.WaitAsync(Timeout)).IsEqualTo(many ? "x,,x" : "x");
    }

    [Test]
    public async Task ConcurrentThreadPoolCallersPublishExactlyOneProducer()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = 32;
        var callers = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            var pending = client.GetStringAsync("key");
            if (Interlocked.Decrement(ref remaining) == 0) joined.SetResult();
            return await pending;
        })).ToArray();
        await joined.Task.WaitAsync(Timeout);
        await BarrierAsync(server, client, 1);
        await server.SendRawAsync("+OK\r\n$1\r\nx\r\n+PONG\r\n"u8.ToArray());
        var results = await Task.WhenAll(callers).WaitAsync(Timeout);
        await Assert.That(results.All(value => value == "x")).IsTrue();
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    public async Task CallerConversionFailureDoesNotAffectOtherWaiters()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server);
        var invalid = client.Strings.GetAsync<int>("key").AsTask();
        var valid = client.GetStringAsync("key").AsTask();
        await BarrierAsync(server, client, 1);
        await server.SendRawAsync("+OK\r\n$1\r\nx\r\n+PONG\r\n"u8.ToArray());
        await Assert.That(async () => await invalid.WaitAsync(Timeout)).Throws<FormatException>();
        await Assert.That(await valid.WaitAsync(Timeout)).IsEqualTo("x");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("x");
    }

    private static FakeRespServer CreateServer()
        => new(Hello, FakeRespServer.OkReply)
        {
            SuppressReply = command => !command.StartsWith("HELLO ") && !command.StartsWith("CLIENT TRACKING "),
        };

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });

    // PING is queued after every caller. Its arrival proves all preceding wire writes are visible.
    private static async Task BarrierAsync(FakeRespServer server, RespireClient client, int expectedReads)
    {
        _ = client.PingAsync().AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Contains("PING"));
        await Assert.That(server.ReceivedCommands.Count(c => c == "CLIENT CACHING YES")).IsEqualTo(expectedReads);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
