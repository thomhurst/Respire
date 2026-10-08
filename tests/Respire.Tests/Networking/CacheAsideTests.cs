using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CacheAsideTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(12);

    [Test]
    [NotInParallel]
    public async Task HotPrimitiveHitsAllocateNothingWithPositiveControl()
    {
        await using var server = new CacheServer();
        server.Set("zero", "0");
        await using var client = await server.ConnectAsync();
        await client.GetOrSetAsync<int>("zero", _ => throw new Exception("factory"), Ttl);
        _ = MeasureHot(client, false);
        _ = MeasureHot(client, true);
        var allocated = AllocationMeasurement.WithoutConcurrentGc(() => MeasureHot(client, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => MeasureHot(client, true));
        await Assert.That(allocated).IsEqualTo(0L);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000L);
    }

    private static object? _escape;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureHot(RespireClient client, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            _ = client.GetOrSetAsync<int>("zero", static _ => throw new Exception("factory"), Ttl).GetAwaiter().GetResult();
            if (allocate) Volatile.Write(ref _escape, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DifferentTypesOrTtlsDoNotShareFactories(bool differentType)
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        var entered = NewSignal();
        var release = NewSignal();
        var calls = 0;
        async ValueTask<string?> Factory(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "value";
        }
        async Task<string?> ReadSecondAsync()
        {
            if (!differentType) return await client.GetOrSetAsync("key", Factory, Ttl + TimeSpan.FromMilliseconds(1));
            var bytes = await client.GetOrSetAsync<byte[]>("key", async token =>
                Encoding.UTF8.GetBytes((await Factory(token))!), Ttl);
            return Encoding.UTF8.GetString(bytes!);
        }
        var first = client.GetOrSetAsync("key", Factory, Ttl).AsTask();
        var second = ReadSecondAsync();
        try { await entered.Task.WaitAsync(Limit); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(Limit);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(server.SuccessfulSets).IsEqualTo(1);
    }

    [Test]
    public async Task RequiresTrackingCacheBeforeCallingFactory()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync(enableCache: false);
        await Assert.That(async () => await client.GetOrSetAsync<string>("key", _ => throw new Exception("factory"), Ttl))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(server.Commands.Any(command => command.StartsWith("GET "))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnsupportedConditionalSetPreservesServerErrorWithoutReplayingFactory(bool coalesce)
    {
        await using var server = new CacheServer();
        var supportedReply = server.Server.ReplyOverride!;
        server.Server.ReplyOverride = (connection, command) => command.StartsWith("SET ")
            ? "-ERR syntax error\r\n"u8.ToArray() : supportedReply(connection, command);
        await using var client = await server.ConnectAsync(coalesce);
        var calls = 0;
        var error = await Assert.That(async () => await client.GetOrSetAsync<string>("key", _ =>
        {
            calls++;
            return ValueTask.FromResult<string?>("created");
        }, Ttl)).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("ERR");
        await Assert.That(error.Message).Contains("syntax error");
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(server.Commands.Count(command => command.StartsWith("SET "))).IsEqualTo(1);
        await Assert.That(server.SuccessfulSets).IsEqualTo(0);
    }

    [Test]
    public async Task StoredDefaultsAndSerializedNullDoNotInvokeFactory()
    {
        await using var server = new CacheServer();
        server.Set("zero", "0");
        server.Set("null", "null");
        await using var client = await server.ConnectAsync();
        await Assert.That(await client.GetOrSetAsync<int>("zero", _ => throw new Exception("factory"), Ttl)).IsEqualTo(0);
        await Assert.That(await client.GetOrSetAsync<int>("zero", _ => throw new Exception("factory"), Ttl)).IsEqualTo(0);
        await Assert.That(await client.GetOrSetAsync<Payload>("null", _ => throw new Exception("factory"), Ttl)).IsNull();
        await Assert.That(server.Commands.Count(command => command.StartsWith("GET "))).IsEqualTo(2);
        await Assert.That(server.Commands.Any(command => command.StartsWith("SET "))).IsFalse();
    }

    [Test]
    public async Task NullFactoryResultIsNotStoredOrMemoized()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        var calls = 0;
        ValueTask<string?> Factory(CancellationToken _) { calls++; return ValueTask.FromResult<string?>(null); }
        await Assert.That(await client.GetOrSetAsync("key", Factory, Ttl)).IsNull();
        await Assert.That(await client.GetOrSetAsync("key", Factory, Ttl)).IsNull();
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(server.Commands.Any(command => command.StartsWith("SET "))).IsFalse();
        await Assert.That(client.ClientSideCache!.GetStatistics().Misses).IsEqualTo(1L);
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(1L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentFactoriesFollowCoalescingOptionAndReturnOwnedValues(bool coalesce)
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync(coalesce);
        var entered = NewSignal();
        var release = NewSignal();
        var calls = 0;
        var expected = coalesce ? 1 : 16;
        async ValueTask<byte[]?> Factory(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == expected) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "value"u8.ToArray();
        }
        var reads = Enumerable.Range(0, 16).Select(_ => client.GetOrSetAsync("key", Factory, Ttl).AsTask()).ToArray();
        try { await entered.Task.WaitAsync(Limit); }
        finally { release.TrySetResult(); }
        var values = await Task.WhenAll(reads).WaitAsync(Limit);
        await Assert.That(calls).IsEqualTo(expected);
        await Assert.That(server.Commands.Count(command => command == "SET key value PX 12000 NX GET")).IsEqualTo(expected);
        values[0]![0] = (byte)'X';
        await Assert.That(Encoding.UTF8.GetString(values[1]!)).IsEqualTo("value");
        // Public completion can precede the receive owner's final release. Cache insertion
        // becomes eligible only after every overlapping native mutation has retired.
        await WaitUntilAsync(() => client.Core.ClientCache!.InspectForTests().ActiveMutationCount == 0);
        var populated = await client.GetOrSetAsync<byte[]>("key", _ => throw new Exception("factory"), Ttl);
        populated![0] = (byte)'Y';
        var getCount = server.Commands.Count(command => command == "GET key");
        await Assert.That(Encoding.UTF8.GetString((await client.GetOrSetAsync<byte[]>("key", _ => throw new Exception("factory"), Ttl))!))
            .IsEqualTo("value");
        await Assert.That(server.Commands.Count(command => command == "GET key")).IsEqualTo(getCount);
    }

    [Test]
    public async Task ConcurrentWriterWinsWithoutReplacingItsValueOrTtl()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        var entered = NewSignal();
        var release = NewSignal();
        var read = client.GetOrSetAsync<string>("key", async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "loser";
        }, Ttl).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            server.Set("key", "winner");
        }
        finally { release.TrySetResult(); }
        await Assert.That(await read.WaitAsync(Limit)).IsEqualTo("winner");
        await Assert.That(server.SuccessfulSets).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("winner");
    }

    [Test]
    public async Task BinaryKeyIsSnapshottedBeforeFactoryAndPrefixIsAppliedOnce()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        var view = client.WithKeyPrefix("tenant:");
        byte[] bytes = [0, 255, 1];
        byte[] physicalKey = [.. "tenant:"u8, .. bytes];
        var invalidated = new TaskCompletionSource<RespireClientCacheInvalidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = view.ClientSideCache!.SubscribeInvalidations(physicalKey, change =>
        {
            if (change.Reasons.HasFlag(RespireClientCacheInvalidationReason.LocalMutation)) invalidated.TrySetResult(change);
        });
        var entered = NewSignal();
        var release = NewSignal();
        var read = view.GetOrSetAsync<string>(bytes, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "value";
        }, Ttl).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            Array.Fill(bytes, (byte)42);
        }
        finally { release.TrySetResult(); }
        await Assert.That(await read.WaitAsync(Limit)).IsEqualTo("value");
        var change = await invalidated.Task.WaitAsync(Limit);
        await Assert.That(change.Key).IsEqualTo((RespireKey)physicalKey);
        var frame = server.Server.ReceivedArguments.Single(arguments => arguments[0].AsSpan().SequenceEqual("SET"u8));
        await Assert.That(frame[1]).IsEquivalentTo(new byte[] { 116, 101, 110, 97, 110, 116, 58, 0, 255, 1 });
    }

    [Test]
    public async Task FactoryFailureFansOutAndLaterCallCanRetry()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        var entered = NewSignal();
        var release = NewSignal();
        async ValueTask<string?> Factory(CancellationToken token)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new InvalidOperationException("factory failed");
        }
        var first = client.GetOrSetAsync("key", Factory, Ttl).AsTask();
        var second = client.GetOrSetAsync("key", Factory, Ttl).AsTask();
        try { await entered.Task.WaitAsync(Limit); }
        finally { release.TrySetResult(); }
        await Assert.That(async () => await first.WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(async () => await second.WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(server.SuccessfulSets).IsEqualTo(0);
        await Assert.That(await client.GetOrSetAsync<string>("key", _ => ValueTask.FromResult<string?>("retry"), Ttl)).IsEqualTo("retry");
    }

    [Test]
    public async Task CancelingOneWaiterDoesNotCancelRemainingFactory()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        using var canceled = new CancellationTokenSource();
        var entered = NewSignal();
        var release = NewSignal();
        CancellationToken factoryToken = default;
        async ValueTask<string?> Factory(CancellationToken token)
        {
            factoryToken = token;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "value";
        }
        var first = client.GetOrSetAsync("key", Factory, Ttl, canceled.Token).AsTask();
        var second = client.GetOrSetAsync("key", Factory, Ttl).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            canceled.Cancel();
            await Assert.That(async () => await first.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(factoryToken.IsCancellationRequested).IsFalse();
        }
        finally { release.TrySetResult(); }
        await Assert.That(await second.WaitAsync(Limit)).IsEqualTo("value");
    }

    [Test]
    public async Task LastCanceledWaiterPreventsLateFactoryWriteEvenWhenFactoryIgnoresToken()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        using var canceled = new CancellationTokenSource();
        var entered = NewSignal();
        var release = NewSignal();
        var factoryCanceled = NewSignal();
        var read = client.GetOrSetAsync<string>("key", async token =>
        {
            using var registration = token.Register(() => factoryCanceled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return "late";
        }, Ttl, canceled.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            canceled.Cancel();
            await Assert.That(async () => await read.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await factoryCanceled.Task.WaitAsync(Limit);
        }
        finally { release.TrySetResult(); }
        await WaitUntilAsync(() => client.Core.ClientCache!.ActiveSharedReadCount == 0);
        await Assert.That(server.Commands.Any(command => command.StartsWith("SET "))).IsFalse();
    }

    [Test]
    public async Task InvalidationRetiresFactoryJoiningButConditionalWritePreservesWinner()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        var firstEntered = NewSignal();
        var secondEntered = NewSignal();
        var firstRelease = NewSignal();
        var secondRelease = NewSignal();
        var first = client.GetOrSetAsync<string>("key", async token =>
        {
            firstEntered.TrySetResult(); await firstRelease.Task.WaitAsync(token); return "first";
        }, Ttl).AsTask();
        Task<string?>? second = null;
        try
        {
            await firstEntered.Task.WaitAsync(Limit);
            client.ClientSideCache!.Clear();
            second = client.GetOrSetAsync<string>("key", async token =>
            {
                secondEntered.TrySetResult(); await secondRelease.Task.WaitAsync(token); return "second";
            }, Ttl).AsTask();
            await secondEntered.Task.WaitAsync(Limit);
            secondRelease.TrySetResult();
            await Assert.That(await second.WaitAsync(Limit)).IsEqualTo("second");
        }
        finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); }
        await Assert.That(await first.WaitAsync(Limit)).IsEqualTo("second");
        await Assert.That(server.SuccessfulSets).IsEqualTo(1);
    }

    [Test]
    public async Task DisposalCancelsSharedFactoryAndDoesNotWrite()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        var entered = NewSignal();
        var read = client.GetOrSetAsync<string>("key", async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        }, Ttl).AsTask();
        await entered.Task.WaitAsync(Limit);
        await client.DisposeAsync();
        await Assert.That(async () => await read.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(server.SuccessfulSets).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingFactoryCancellationCallbackCannotInterruptCleanup(bool disposeClient)
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        using var canceled = new CancellationTokenSource();
        var entered = NewSignal();
        var release = NewSignal();
        var read = client.GetOrSetAsync<string>("key", async token =>
        {
            using var registration = token.Register(static () => throw new InvalidOperationException("cancellation callback"));
            entered.TrySetResult();
            await release.Task;
            return "late";
        }, Ttl, canceled.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            if (disposeClient)
            {
                await client.DisposeAsync();
                await Assert.That(async () => await read.WaitAsync(Limit)).ThrowsExactly<AggregateException>();
            }
            else
            {
                canceled.Cancel();
                await Assert.That(async () => await read.WaitAsync(Limit)).Throws<OperationCanceledException>();
            }
        }
        finally { release.TrySetResult(); }
        await WaitUntilAsync(() => client.Core.ClientCache!.ActiveSharedReadCount == 0);
        await Assert.That(server.SuccessfulSets).IsEqualTo(0);
    }

    [Test]
    public async Task RejectsInvalidInputsBeforeFactoryOrWireWork()
    {
        await using var server = new CacheServer();
        await using var client = await server.ConnectAsync();
        await Assert.That(async () => await client.GetOrSetAsync<string>("key", null!, Ttl)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(async () => await client.GetOrSetAsync<string>("key", _ => throw new Exception(), TimeSpan.FromTicks(1)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.GetOrSetAsync<string>("key", _ => throw new Exception(), Ttl, new CancellationToken(true)))
            .Throws<OperationCanceledException>();
        await Assert.That(server.Commands.Any(command => command.StartsWith("GET ") || command.StartsWith("SET "))).IsFalse();
    }

    public sealed class Payload { public string? Name { get; set; } }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class CacheServer : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, byte[]> _values = new();
        private int _successfulSets;
        internal FakeRespServer Server { get; }
        internal IReadOnlyList<string> Commands => Server.ReceivedCommands;
        internal int SuccessfulSets => Volatile.Read(ref _successfulSets);

        internal CacheServer()
        {
            Server = new FakeRespServer(FakeRespServer.OkReply);
            Server.ReplyOverride = (_, command) =>
            {
                if (command == "HELLO 3") return "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
                if (command == "CLIENT ID") return ":1\r\n"u8.ToArray();
                if (command.StartsWith("CLIENT KILL")) return ":0\r\n"u8.ToArray();
                if (command == "PING") return FakeRespServer.PongReply;
                if (!command.StartsWith("GET ") && !command.StartsWith("SET ")) return FakeRespServer.OkReply;
                var arguments = Server.ReceivedArguments[^1];
                var key = Convert.ToHexString(arguments[1]);
                if (command.StartsWith("GET ")) return Bulk(_values.GetValueOrDefault(key));
                if (_values.TryAdd(key, arguments[2].ToArray()))
                {
                    Interlocked.Increment(ref _successfulSets);
                    return Bulk(null);
                }
                return Bulk(_values[key]);
            };
        }

        internal void Set(string key, string value) => _values[Convert.ToHexString(Encoding.UTF8.GetBytes(key))] = Encoding.UTF8.GetBytes(value);
        internal ValueTask<RespireClient> ConnectAsync(bool coalesce = true, bool enableCache = true) => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", Server.Port)], Connections = 1,
            ClientSideCache = enableCache ? new() { CoalesceConcurrentMisses = coalesce } : null,
        });
        private static byte[] Bulk(byte[]? value) => value is null ? "$-1\r\n"u8.ToArray()
            : [.. Encoding.ASCII.GetBytes($"${value.Length}\r\n"), .. value, 13, 10];
        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }
}
