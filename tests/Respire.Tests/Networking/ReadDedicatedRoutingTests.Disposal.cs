using System.Reflection;
using Microsoft.Extensions.Logging;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class ReadDedicatedRoutingTests
{
    [Test]
    public async Task RepeatedPrimaryRetirementStopsBeforeCallerCancellation()
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], RespireReadFrom.PrimaryPreferred));
        var pool = await client.Core.GetDedicatedPoolAsync(CancellationToken.None);
        await pool.RetireAsync();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.That(async () => await client.Core.ReadRouter.RentDedicatedConnectionAsync(
            RespireReadFrom.PrimaryPreferred, caller.Token)).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(caller.IsCancellationRequested).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RemovedReplicaCleanupRemainsOwnedUntilCompletion(bool cleanupFails, bool finishBeforeRouterDispose)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        FakeRespServer[] replicas = [replica];
        await using var sentinel = Sentinel(primary, () => Volatile.Read(ref replicas));
        using var logger = new PausedReplicaCleanupLogger(replica.Port, cleanupFails);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], RespireReadFrom.ReplicaPreferred)
            with { SentinelPrimaryName = "primary", LoggerFactory = logger });
        var router = client.Core.ReadRouter;
        var lease = await router.RentDedicatedConnectionAsync(RespireReadFrom.ReplicaPreferred, CancellationToken.None);
        lease.Pool.Return(lease.Connection);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.SuppressReply = command =>
        {
            if (!command.StartsWith("XREAD ")) return false;
            arrived.TrySetResult();
            return true;
        };
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 0, "STREAMS", "key", "0"]).AsTask();
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref replicas, []);
            await router.RefreshNowAsync(CancellationToken.None);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!lease.Pool.IsStopping) await Task.Delay(5, limit.Token);
            logger.Arm();
            // Run the first disposal step separately to make retirement's cleanup start before
            // the router takes its ownership snapshot. No task scheduling race is required.
            var lifetime = (CancellationTokenSource)typeof(ReadEndpointRouter)
                .GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(router)!;
            await lifetime.CancelAsync();
            await logger.Started.Task.WaitAsync(limit.Token);
            if (finishBeforeRouterDispose)
            {
                logger.Release.Set();
                await logger.FailureLogged.Task.WaitAsync(limit.Token);
                var retiring = typeof(ReadEndpointRouter)
                    .GetField("_retiring", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(router)!;
                await Assert.That((int)retiring.GetType().GetProperty("Count")!.GetValue(retiring)!).IsEqualTo(0);
            }
            var disposal = router.DisposeAsync().AsTask();
            try { if (!finishBeforeRouterDispose) await Assert.That(disposal.IsCompleted).IsFalse(); }
            finally { logger.Release.Set(); }
            if (cleanupFails)
            {
                var error = await Assert.That(async () => await disposal.WaitAsync(limit.Token))
                    .ThrowsExactly<InvalidOperationException>();
                await Assert.That(error).IsSameReferenceAs(logger.Failure);
            }
            else await disposal.WaitAsync(limit.Token);
            var commandError = await Assert.That(async () => await pending.WaitAsync(limit.Token)).Throws<Exception>();
            await Assert.That(commandError is RespireConnectionException
                || cleanupFails && ReferenceEquals(commandError, logger.Failure)).IsTrue();
            await Assert.That(lease.Connection.IsConnected).IsFalse();
        }
        finally
        {
            logger.Release.Set();
            try { using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is RespireConnectionException || ReferenceEquals(error, logger.Failure)) { }
        }
    }

    [Test]
    public async Task ConcurrentReplicaDisposalSharesCleanupFailure()
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        using var logger = new PausedReplicaCleanupLogger(replica.Port, true);
        var client = await RespireClient.ConnectAsync(Options(primary, [replica], RespireReadFrom.Replica)
            with { LoggerFactory = logger });
        try
        {
            var router = client.Core.ReadRouter;
            var selection = await router.SelectAsync(RespireReadFrom.Replica, CancellationToken.None);
            var lease = await router.RentDedicatedConnectionAsync(RespireReadFrom.Replica, CancellationToken.None);
            lease.Pool.Return(lease.Connection);
            logger.Arm();
            var first = Task.Run(async () => await selection.Replica!.DisposeAsync());
            await logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = selection.Replica!.DisposeAsync().AsTask();
            await Assert.That(first.IsCompleted).IsFalse();
            await Assert.That(second.IsCompleted).IsFalse();
            logger.Release.Set();
            foreach (var disposal in new[] { first, second })
            {
                var error = await Assert.That(async () => await disposal.WaitAsync(TimeSpan.FromSeconds(5)))
                    .ThrowsExactly<InvalidOperationException>();
                await Assert.That(error).IsSameReferenceAs(logger.Failure);
            }
        }
        finally
        {
            logger.Release.Set();
            try { await client.DisposeAsync(); }
            catch (Exception error) when (ReferenceEquals(error, logger.Failure)) { }
        }
    }

    private sealed class PausedReplicaCleanupLogger(int port, bool fail) : ILogger, ILoggerFactory
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FailureLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal readonly InvalidOperationException Failure = new("Injected replica cleanup failure.");
        private int _armed;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug && formatter(state, exception) == "Closing a removed read replica failed")
                FailureLogged.TrySetResult();
            if (logLevel == LogLevel.Debug && formatter(state, exception) == $"Disconnected from 127.0.0.1:{port}"
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Started.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cleanup gate was not released.");
                if (fail) throw Failure;
            }
            if (fail && logLevel == LogLevel.Warning) throw Failure;
        }
    }
}
