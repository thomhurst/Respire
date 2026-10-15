using System.Net.Sockets;
using System.Diagnostics.Metrics;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class UnitRetryLifetimeTests
{
    public static IEnumerable<(string, bool)> RetirementCases()
    {
        foreach (var owner in new[] { "immediate", "batch", "transaction", "cache" })
        foreach (var timeout in new[] { false, true })
            yield return (owner, timeout);
    }

    [Test]
    [MethodDataSource(nameof(RetirementCases))]
    public async Task LateRetriedReplyCannotCompleteNewOwnerOrChangeTerminalResult(string owner, bool timeout)
    {
        var attempts = 0;
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = owner == "transaction" ? "EXEC" : "GET key";
        await using var server = UnitServer((command, _) => command == "GET key" ? TryAgain : null,
            _ => "*1\r\n-TRYAGAIN busy\r\n"u8.ToArray());
        // Suppression keeps reading commands, unlike a reply gate. Once the second attempt
        // is parked, every later reply is injected explicitly in FIFO order.
        server.SuppressReply = command =>
        {
            if (command == target)
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 2) parked.TrySetResult();
                if (attempt == 3) nextSeen.TrySetResult();
            }
            return Volatile.Read(ref attempts) >= 2;
        };
        await using var client = await RespireClient.ConnectAsync(LifetimeOptions(server, owner) with
        { CommandTimeout = TimeSpan.FromSeconds(timeout ? 2 : 15) });
        using var cancellation = new CancellationTokenSource();
        await using var first = new ObservedRead(client, owner, cancellation.Token);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ring = client.Core.Multiplexer.GetConnection().InspectForTests().Inflight;
        // EXEC's prefix replies may still be retiring. The target becomes the head before
        // cancellation/timeout can release the caller's reference.
        await WaitUntilAsync(() => ring.Count == 1);
        await Assert.That(ring.TryPeek(out var oldSource)).IsTrue();
        if (!timeout) cancellation.Cancel();
        var failure = await Failure(async () => await first.Result.WaitAsync(TimeSpan.FromSeconds(5)));
        if (timeout) await Assert.That(failure).IsTypeOf<RespireTimeoutException>();
        else
        {
            await Assert.That(failure).IsAssignableTo<OperationCanceledException>();
            await Assert.That(((OperationCanceledException)failure).CancellationToken).IsEqualTo(cancellation.Token);
        }
        await Assert.That(oldSource!.InspectForTests().ReferenceCount).IsGreaterThan(0);
        await first.AssertSingleCompletionAsync();

        // The old logical operation has finished, but its receive owner is still live.
        // A new same-key cache read must create a new producer after the last waiter left.
        await using var next = new ObservedRead(client, owner, default);
        await nextSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(next.Result.IsCompleted).IsFalse();
        var connection = server.ReceivedConnectionIds[^1];
        await server.SendRawAsync(owner == "transaction" ? "*1\r\n$4\r\nlate\r\n"u8.ToArray() : Bulk("late"), connection);
        // Receipt of only the old reply cannot complete the new operation. A separate
        // marker isn't available inside FIFO, so the distinct final value is decisive.
        await server.SendRawAsync(owner switch
        {
            "transaction" => "+OK\r\n+QUEUED\r\n*1\r\n$5\r\nvalue\r\n"u8.ToArray(),
            "cache" => "+OK\r\n$5\r\nvalue\r\n"u8.ToArray(),
            _ => Value,
        }, connection);
        await Assert.That(await next.Result.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("value");
        server.SuppressReply = null;
        server.ReplyOverride = (_, command) => command == "GET key" ? Value : FakeRespServer.OkReply;
        // Consume several more public calls to exercise subsequent pool generations.
        for (var i = 0; i < 8; i++)
            await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        // Release the barrier's raw payload too. Its await can resume before the completion
        // runner releases the pending-response source, which is checked separately below.
        using (var barrier = await client.ExecuteAsync(RespireCommands.Connection.PING))
            await Assert.That(barrier.AsString()).IsEqualTo("OK");
        // A completion can run our continuation inline before the receive runner releases
        // its own reference. FIFO dequeue alone is therefore not a quiescence barrier.
        await WaitUntilAsync(() => ring.Count == 0 && oldSource.InspectForTests().ReferenceCount == 0
            && (client.Core.ClientCache?.ActiveSharedReadCount ?? 0) == 0);
        await first.AssertSingleCompletionAsync();
        await next.AssertSingleCompletionAsync();
        await Assert.That(await Failure(async () => await first.Result)).IsSameReferenceAs(failure);
        // Quiescent ownership snapshots, not a claim that the entire heap was reclaimed.
        await Assert.That(oldSource.InspectForTests().ReferenceCount).IsEqualTo(0);
        await Assert.That(oldSource.InspectForTests().RegisteredCancellationToken.CanBeCanceled).IsFalse();
        if (client.Core.ClientCache is { } cache)
        {
            await Assert.That(cache.InspectForTests().PendingQueryDependencyCount).IsEqualTo(0);
            await Assert.That(cache.InspectForTests().IdleQueryStorage.RetainedDependencies).IsEqualTo(0);
        }
    }

    [Test]
    [MethodDataSource(nameof(RetirementCases))]
    public async Task StoppedOwnerCannotResumeWhenReplacementDialIsReleased(string owner, bool dispose)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = 0;
        RespireConnection? previousConnection = null;
        Task? retiring = null;
        var dialing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = UnitServer((command, _) => command == "GET key" ? TryAgain : null,
            _ => "*1\r\n-TRYAGAIN busy\r\n"u8.ToArray());
        using var listener = RetryHandledListener(handled, () =>
        {
            // A selected server error identifies command retry admission; connection-error
            // telemetry can also come from the independent transport lifecycle. Dispose
            // the connection here: unlike peer EOF, this marks it unavailable synchronously
            // before the owner can select it for another attempt. Await cleanup outside
            // the callback so an inline completion runner is free to retire its source.
            Volatile.Write(ref lost, 1);
            retiring = previousConnection!.DisposeAsync().AsTask();
        });
        await using var client = await RespireClient.ConnectAsync(LifetimeOptions(server, owner) with
        {
            TestingStreamFactory = async (host, port, token) =>
            {
                if (Volatile.Read(ref lost) != 0)
                {
                    dialing.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(host, port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
        });
        previousConnection = client.Core.Multiplexer.GetConnection();
        using var cancellation = new CancellationTokenSource();
        await using var pending = new ObservedRead(client, owner, cancellation.Token);
        try
        {
            await dialing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(pending.Result.IsCompleted).IsFalse();
            if (dispose) await client.DisposeAsync();
            else cancellation.Cancel();
            var failure = await Failure(async () => await pending.Result.WaitAsync(TimeSpan.FromSeconds(5)));
            await retiring!.WaitAsync(TimeSpan.FromSeconds(5));
            // ClientCore cancels its lifetime before StopSharedReads cancels the producer.
            // Either wakeup can win for a cache waiter, but neither may complete it twice.
            if (dispose && owner == "cache")
                await Assert.That(failure is ObjectDisposedException or OperationCanceledException).IsTrue().Because(failure.ToString());
            else if (dispose) await Assert.That(failure).IsAssignableTo<ObjectDisposedException>();
            else
            {
                await Assert.That(failure).IsAssignableTo<OperationCanceledException>();
                await Assert.That(((OperationCanceledException)failure).CancellationToken).IsEqualTo(cancellation.Token);
            }
            server.ReplyOverride = (_, command) => command == "GET key" ? Value
                : command.StartsWith("HELLO", StringComparison.Ordinal) ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply;
            release.TrySetResult();
            if (!dispose)
                await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("value");
            await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(dispose ? 1 : 2);
            await pending.AssertSingleCompletionAsync();
            await Assert.That(await Failure(async () => await pending.Result)).IsSameReferenceAs(failure);
        }
        finally { release.TrySetResult(); }
    }

    private static RespireOptions LifetimeOptions(FakeRespServer server, string owner) => Options(server, sentinel: false) with
    {
        Protocol = owner == "cache" ? RespireProtocol.Resp3 : RespireProtocol.Resp2,
        ClientSideCache = owner == "cache" ? new() { CoalesceConcurrentMisses = true } : null,
        CommandTimeout = TimeSpan.FromSeconds(15),
    };

    [Test]
    public async Task LastCoalescedWaiterCancelsRetryBackoffBeforeSameKeyProducerStarts()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = RetryHandledListener(handled);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var server = UnitServer((command, _) => command == "GET key"
            ? Interlocked.Increment(ref attempts) == 1 ? TryAgain : Value : null, _ => null);
        server.GateCommand("GET key", release.Task);
        await using var client = await RespireClient.ConnectAsync(LifetimeOptions(server, "cache") with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(10), Jitter = 0 } });
        using var leaderToken = new CancellationTokenSource();
        using var followerToken = new CancellationTokenSource();
        await using var leader = new ObservedRead(client, "cache", leaderToken.Token);
        await using var follower = new ObservedRead(client, "cache", followerToken.Token);
        release.TrySetResult();
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        leaderToken.Cancel();
        var leaderError = await Failure(async () => await leader.Result.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.That(((OperationCanceledException)leaderError).CancellationToken).IsEqualTo(leaderToken.Token);
        await Assert.That(follower.Result.IsCompleted).IsFalse();
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(1);
        followerToken.Cancel();
        var followerError = await Failure(async () => await follower.Result.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.That(((OperationCanceledException)followerError).CancellationToken).IsEqualTo(followerToken.Token);
        // Finished producer ownership, rather than elapsed time, is the no-resend barrier.
        await WaitUntilAsync(() => client.Core.ClientCache.ActiveSharedReadCount == 0);
        await Assert.That(attempts).IsEqualTo(1);
        await using var next = new ObservedRead(client, "cache", default);
        await Assert.That(await next.Result.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(attempts).IsEqualTo(2);
        await leader.AssertSingleCompletionAsync();
        await follower.AssertSingleCompletionAsync();
        await next.AssertSingleCompletionAsync();
    }

    private static MeterListener RetryHandledListener(TaskCompletionSource signal, Action? beforeResend = null)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        { if (instrument.Name == "redis.client.errors") meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var handled = false;
            var selectedError = false;
            foreach (var tag in tags)
            {
                if (tag.Key == "redis.client.errors.internal" && Equals(tag.Value, true)) handled = true;
                if (tag.Key == "db.response.status_code" && Equals(tag.Value, "TRYAGAIN")) selectedError = true;
            }
            if (!handled || !selectedError || signal.Task.IsCompleted) return;
            beforeResend?.Invoke();
            signal.TrySetResult();
        });
        listener.Start();
        return listener;
    }

    // Count the actual public awaiter callbacks, before TaskCompletionSource can hide a
    // duplicate completion. Consume each ValueTask once; deferred members are synchronous
    // result handles and must only be inspected after their unit finishes.
    private sealed class ObservedRead : IAsyncDisposable
    {
        private readonly List<Func<int>> _counts = [];
        private readonly RespireBatch? _batch;
        private readonly RespireTransaction? _transaction;
        private readonly RespirePending<string?>? _member;
        private (RespirePendingStatus Status, Exception? Error, string? Value)? _memberSnapshot;
        internal Task<string?> Result { get; }

        internal ObservedRead(RespireClient client, string owner, CancellationToken token)
        {
            if (owner is "immediate" or "cache")
            {
                var awaiter = client.GetStringAsync("key", token).GetAwaiter();
                Result = Watch(awaiter.IsCompleted, awaiter.UnsafeOnCompleted, awaiter.GetResult);
                return;
            }
            RespirePending<string?> member;
            Task execution;
            if (owner == "batch")
            {
                _batch = client.CreateBatch();
                member = _batch.GetString("key");
                var awaiter = _batch.TryExecuteAsync(token).GetAwaiter();
                execution = Watch(awaiter.IsCompleted, awaiter.UnsafeOnCompleted, awaiter.GetResult);
            }
            else
            {
                _transaction = client.CreateTransaction();
                member = _transaction.GetString("key");
                var awaiter = _transaction.CommitAsync(token).GetAwaiter();
                execution = Watch(awaiter.IsCompleted, awaiter.UnsafeOnCompleted, () => { awaiter.GetResult(); return true; });
            }
            _member = member;
            Result = FinishAsync(execution, member);
        }

        private Task<T> Watch<T>(bool completed, Action<Action> register, Func<T> consume)
        {
            var count = 0;
            _counts.Add(() => Volatile.Read(ref count));
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Complete()
            {
                if (Interlocked.Increment(ref count) != 1) return;
                try { result.TrySetResult(consume()); }
                catch (Exception error) { result.TrySetException(error); }
            }
            if (completed) Complete(); else register(Complete);
            return result.Task;
        }

        private static async Task<string?> FinishAsync(Task execution, RespirePending<string?> member)
        {
            Exception? executionError = null;
            try { await execution; } catch (Exception error) { executionError = error; }
            if (executionError is not null)
            {
                var memberError = await Failure(async () => await member);
                await Assert.That(memberError.GetType()).IsEqualTo(executionError.GetType());
                throw executionError;
            }
            return await member;
        }

        internal async Task AssertSingleCompletionAsync()
        {
            foreach (var count in _counts) await Assert.That(count()).IsEqualTo(1);
            if (_member is not null)
            {
                var snapshot = (_member.Status, _member.Error, _member.HasResult ? _member.Result : null);
                if (_memberSnapshot is { } previous) await Assert.That(snapshot).IsEqualTo(previous);
                _memberSnapshot = snapshot;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _batch?.Dispose();
            if (_transaction is not null) await _transaction.DisposeAsync();
        }
    }
}
