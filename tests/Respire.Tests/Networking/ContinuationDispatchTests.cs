using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ContinuationDispatchTests
{
    [Test]
    [Arguments("raw", true)]
    [Arguments("string", true)]
    [Arguments("bytes", true)]
    [Arguments("converted", true)]
    [Arguments("raw", false)]
    [Arguments("string", false)]
    [Arguments("bytes", false)]
    [Arguments("converted", false)]
    [Arguments("transaction", false)]
    public async Task AsyncContinuationDoesNotHoldOtherCallers(string responseKind, bool native)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = command =>
            {
                if (command is not ("PING" or "MULTI" or "EXEC")) return false;
                sent.TrySetResult();
                return true;
            },
        };
        var options = new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
            ContinuationDispatch = RespireContinuationDispatch.ThreadPool,
            // Progress must not depend on the stall rescue timer.
            StalledDeliveryThreshold = TimeSpan.FromHours(1),
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            options.ToConnectionOptions());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A late continuation may still be running after a failed assertion.
        var release = new ManualResetEventSlim();
        var command = new RawCommand(FakeRespServer.PingFrame);
        switch (responseKind)
        {
            case "raw":
                Register(native ? connection.SendNativeCheckedAsync(command, default, "PING") : connection.SendAsync(command),
                    static value => value.Dispose());
                break;
            case "string":
                Register(native ? connection.SendNativeStringAsync(command, default, "PING") : connection.SendStringAsync(command),
                    static _ => { });
                break;
            case "bytes":
                Register(native ? connection.SendNativeBytesAsync(command, default, "PING") : connection.SendBytesAsync(command),
                    static _ => { });
                break;
            case "transaction":
                Register(connection.SendTransactionAsync(FakeRespServer.PingFrame, commandCount: 1), static value => value.Dispose());
                break;
            default:
                Register(native
                    ? connection.SendNativeConvertedAsync(command, default, "PING", 0,
                        static (int _, in Respire.Protocol.RespValue value) => value.AsString(), false)
                    : connection.SendConvertedAsync(command, 0,
                        static (int _, in Respire.Protocol.RespValue value) => value.AsString(), false), static _ => { });
                break;
        }
        try
        {
            // Register before the reply, excluding the already-completed await case.
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server.SendRawAsync(responseKind == "transaction"
                ? "+OK\r\n+QUEUED\r\n*1\r\n+PONG\r\n"u8.ToArray() : FakeRespServer.PongReply);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            server.SuppressReply = null;
            using var second = await connection.SendAsync(command).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(second.AsString()).IsEqualTo("PONG");
            await Assert.That(release.IsSet).IsFalse();
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        void Register<T>(ValueTask<T> pending, Action<T> consume)
        {
            var awaiter = pending.ConfigureAwait(false).GetAwaiter();
            awaiter.UnsafeOnCompleted(() =>
            {
                try
                {
                    consume(awaiter.GetResult());
                    entered.TrySetResult();
                    if (responseKind == "converted")
                    {
                        // Ordinary CPU work after an await must also leave delivery independent.
                        var work = System.Diagnostics.Stopwatch.StartNew();
                        while (!release.IsSet && work.Elapsed < TimeSpan.FromSeconds(15)) Thread.SpinWait(64);
                    }
                    else release.Wait(TimeSpan.FromSeconds(15));
                    finished.TrySetResult();
                }
                catch (Exception error) { entered.TrySetException(error); finished.TrySetException(error); }
            });
        }
    }

    [Test]
    [Arguments("raw")]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("converted")]
    [Arguments("transaction")]
    public async Task PooledSourcesHonorDispatchSettingAcrossRents(string responseKind)
    {
        // Global pools must restore inline delivery when another client used asynchronous delivery.
        foreach (var asynchronous in new[] { true, false, true, false })
        {
            var completingThread = Environment.CurrentManagedThreadId;
            var returned = 0;
            Task<bool> callback;
            PendingResponse source;
            switch (responseKind)
            {
                case "raw":
                    var raw = new PendingResponsePool(1, asynchronous).Rent();
                    source = raw;
                    callback = Observe(raw.Task, static value => value.Dispose());
                    break;
                case "string":
                    var text = StringPendingResponseSource.Rent("PING", runContinuationsAsynchronously: asynchronous);
                    source = text;
                    callback = Observe(text.Task, static _ => { });
                    break;
                case "bytes":
                    var bytes = BytesPendingResponseSource.Rent("PING", runContinuationsAsynchronously: asynchronous);
                    source = bytes;
                    callback = Observe(bytes.Task, static _ => { });
                    break;
                case "transaction":
                    var multi = MultiReplyPendingResponseSource.Rent(1, 0, "MULTI/EXEC", asynchronous);
                    source = multi;
                    callback = Observe(multi.Task, static value => value.Dispose());
                    break;
                default:
                    var converted = ConvertedPendingResponseSource<int, string?>.Rent(0,
                        static (int _, in RespValue value) => value.AsString(), false, "PING",
                        runContinuationsAsynchronously: asynchronous);
                    source = converted;
                    callback = Observe(converted.Task, static _ => { });
                    break;
            }
            source.TrySetResult(RespValue.SimpleString("PONG"u8.ToArray()));
            Volatile.Write(ref returned, 1);
            source.ReleaseRef();
            await Assert.That(await callback.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(!asynchronous);

            Task<bool> Observe<T>(ValueTask<T> pending, Action<T> consume)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var awaiter = pending.ConfigureAwait(false).GetAwaiter();
                awaiter.UnsafeOnCompleted(() =>
                {
                    try
                    {
                        consume(awaiter.GetResult());
                        completion.TrySetResult(Environment.CurrentManagedThreadId == completingThread && Volatile.Read(ref returned) == 0);
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                });
                return completion.Task;
            }
        }
    }

    [Test]
    public async Task OptionsPreserveDefaultsAndReachControlAndDataConnections()
    {
        var defaults = new RespireOptions { Endpoints = [new("localhost")] }.ValidateAndSnapshot();
        await Assert.That(defaults.ContinuationDispatch).IsEqualTo(RespireContinuationDispatch.Adaptive);
        await Assert.That(defaults.StalledDeliveryThreshold).IsEqualTo(TimeSpan.FromMilliseconds(20));
        var options = defaults with
        {
            ContinuationDispatch = RespireContinuationDispatch.ThreadPool,
            StalledDeliveryThreshold = TimeSpan.FromMilliseconds(25),
        };
        foreach (var connection in new[] { options.ToConnectionOptions(), options.ToControlConnectionOptions() })
        {
            await Assert.That(connection.ContinuationDispatch).IsEqualTo(RespireContinuationDispatch.ThreadPool);
            await Assert.That(connection.StalledDeliveryThreshold).IsEqualTo(options.StalledDeliveryThreshold);
        }
    }

    [Test]
    [Arguments(-1.0)]
    [Arguments(0.0)]
    [Arguments(0.5)]
    [Arguments(4294967295.0)]
    public async Task InvalidStallThresholdIsRejected(double milliseconds)
    {
        await Assert.That(() => new RespireOptions
        {
            Endpoints = [new("localhost")],
            StalledDeliveryThreshold = TimeSpan.FromMilliseconds(milliseconds),
        }.ValidateAndSnapshot()).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(async () => await RespireConnection.ConnectAsync("localhost", 0,
            new RespireConnectionOptions { StalledDeliveryThreshold = TimeSpan.FromMilliseconds(milliseconds) }))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task UndefinedContinuationDispatchIsRejected()
    {
        await Assert.That(() => new RespireOptions
        {
            Endpoints = [new("localhost")],
            ContinuationDispatch = (RespireContinuationDispatch)42,
        }.ValidateAndSnapshot()).ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task AdaptiveDispatchTripsOnlyOnMostlySlowContinuations()
    {
        var slow = CompletionScheduler.AdaptiveDispatch.SlowTicks + 1;
        var window = CompletionScheduler.AdaptiveDispatch.WindowTicks;
        var policy = new CompletionScheduler.AdaptiveDispatch();
        var now = 1_000L;

        // An isolated slow continuation between fast ones never trips the window.
        for (var i = 0; i < 100; i++)
        {
            policy.Record(i % 2 == 0 ? slow : 0, now, window);
            await Assert.That(policy.ShouldDispatch(now)).IsFalse();
        }

        policy = new CompletionScheduler.AdaptiveDispatch();
        for (var i = 1; i < CompletionScheduler.AdaptiveDispatch.TripScore; i++)
        {
            policy.Record(slow, now, window);
            await Assert.That(policy.ShouldDispatch(now)).IsFalse();
        }
        policy.Record(slow, now, window);
        await Assert.That(policy.ShouldDispatch(now)).IsTrue();
        await Assert.That(policy.ShouldDispatch(now + window - 1)).IsTrue();

        // After the window, a still-slow workload re-trips after two inline probes.
        now += window;
        await Assert.That(policy.ShouldDispatch(now)).IsFalse();
        policy.Record(slow, now, window);
        await Assert.That(policy.ShouldDispatch(now)).IsFalse();
        policy.Record(slow, now, window);
        await Assert.That(policy.ShouldDispatch(now)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SlowContinuationsLeaveTheRunnerOnlyWithAdaptiveDispatch(bool adaptive)
    {
        const int count = 16;
        var scheduler = new CompletionScheduler
        {
            AdaptiveDispatchEnabled = adaptive,
            // A runner paused past the production window would correctly probe inline again.
            AdaptiveDispatchWindowTicks = System.Diagnostics.Stopwatch.Frequency * 3600,
        };
        var pool = new PendingResponsePool(count);
        var runnerThread = -1;
        var lastDelivered = 0;
        var offRunner = new bool[count];
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = count;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var source = pool.Rent();
            var awaiter = source.Task.ConfigureAwait(false).GetAwaiter();
            awaiter.UnsafeOnCompleted(() =>
            {
                try
                {
                    using var value = awaiter.GetResult();
                    // The first reply always runs inline, so its thread is the runner.
                    if (index == 0) Volatile.Write(ref runnerThread, Environment.CurrentManagedThreadId);
                    offRunner[index] = Environment.CurrentManagedThreadId != Volatile.Read(ref runnerThread)
                        || Volatile.Read(ref lastDelivered) != 0;
                    if (index == count - 1) Volatile.Write(ref lastDelivered, 1);
                    // CPU work well above the slow threshold.
                    var work = System.Diagnostics.Stopwatch.StartNew();
                    while (work.Elapsed < TimeSpan.FromMilliseconds(1)) Thread.SpinWait(16);
                    if (Interlocked.Decrement(ref remaining) == 0) done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
            });
            scheduler.Add(source, RespValue.Integer(index));
        }
        scheduler.Flush();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var trip = CompletionScheduler.AdaptiveDispatch.TripScore;
        // Replies before the trip are timed inline; the final reply has nothing behind it.
        for (var i = 0; i < trip; i++) await Assert.That(offRunner[i]).IsFalse();
        await Assert.That(offRunner[count - 1]).IsFalse();
        for (var i = trip; i < count - 1; i++) await Assert.That(offRunner[i]).IsEqualTo(adaptive);
    }

    [Test]
    public async Task AdaptiveDispatchDoesNotSurviveReturnToThePool()
    {
        var pool = new PendingResponsePool(1);
        var first = pool.Rent();
        first.DispatchContinuationAsynchronously();
        first.TrySetResult(RespValue.Integer(1));
        first.ReleaseRef();
        using (var value = await first.Task) { }
        var second = pool.Rent();
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        var completingThread = Environment.CurrentManagedThreadId;
        var inline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = second.Task.ConfigureAwait(false).GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            using var value = awaiter.GetResult();
            inline.TrySetResult(Environment.CurrentManagedThreadId == completingThread);
        });
        second.TrySetResult(RespValue.Integer(2));
        second.ReleaseRef();
        await Assert.That(await inline.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
    }
}
