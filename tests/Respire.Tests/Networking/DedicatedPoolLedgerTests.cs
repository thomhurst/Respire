using Microsoft.Extensions.Logging;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DedicatedPoolLedgerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetirementRetainsOwnershipUntilBorrowerReturns(bool idleLease)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var pool = CreatePool(server);
        var ledger = new DedicatedPoolLedger(new());
        ledger.Add(pool);
        var borrowed = await pool.RentAsync(CancellationToken.None);
        RespireConnection? idle = null;
        if (idleLease)
        {
            idle = await pool.RentAsync(CancellationToken.None);
            pool.Return(idle);
        }
        var retirement = ledger.RetireAsync(pool);
        await Assert.That(retirement.IsCompleted).IsFalse();
        await Assert.That(ledger.Count).IsEqualTo(1);
        await Assert.That(borrowed.IsConnected).IsTrue();
        if (idle is not null) await Assert.That(idle.IsConnected).IsFalse();
        pool.Return(borrowed);
        await retirement.WaitAsync(Limit);
        await Assert.That(ledger.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExplicitDisposalAbortsCurrentAndDrainingPools()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var previous = CreatePool(server);
        await using var current = CreatePool(server);
        var ledger = new DedicatedPoolLedger(new());
        ledger.Add(previous);
        ledger.Add(current);
        var oldLease = await previous.RentAsync(CancellationToken.None);
        var newLease = await current.RentAsync(CancellationToken.None);
        var retirement = ledger.RetireAsync(previous);
        await ledger.DisposeAllAsync().WaitAsync(Limit);
        await retirement.WaitAsync(Limit);
        await Assert.That(oldLease.IsConnected).IsFalse();
        await Assert.That(newLease.IsConnected).IsFalse();
        await Assert.That(ledger.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FailedRetirementRemainsOwnedForExplicitDisposal()
    {
        await using var server = new FakeRespServer();
        var logger = new CleanupFailureLogger();
        await using var pool = CreatePool(server, logger);
        var ledger = new DedicatedPoolLedger(new());
        ledger.Add(pool);
        var connection = await pool.RentAsync(CancellationToken.None);
        pool.Return(connection);
        await Assert.That(async () => await ledger.RetireAsync(pool)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(ledger.Count).IsEqualTo(1);
        var warnings = logger.Warnings;
        await ledger.DisposeAllAsync().WaitAsync(Limit);
        await Assert.That(logger.Warnings).IsGreaterThan(warnings);
        await Assert.That(ledger.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StandaloneMovingCleanupFailureRetainsPoolForClientDisposal()
    {
        await using var server = MaintenanceServer();
        await using var target = MaintenanceServer();
        var logger = new CleanupFailureLogger();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Endpoints = [new("127.0.0.1", server.Port)], LoggerFactory = logger,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
        });
        try
        {
            var pool = client.Core.DedicatedPool;
            var connection = await pool.RentAsync(CancellationToken.None, kind: DedicatedLeaseKind.Streaming);
            pool.Return(connection);
            await server.SendRawAsync(System.Text.Encoding.UTF8.GetBytes(
                $">4\r\n+MOVING\r\n:1\r\n:10\r\n+127.0.0.1:{target.Port}\r\n"), server.ReceivedConnectionIds[0]);
            using var timeout = new CancellationTokenSource(Limit);
            while (!pool.IsStopping) await Task.Delay(5, timeout.Token);
            await Assert.That(ReferenceEquals(client.Core.DedicatedPool, pool)).IsFalse();
            await Assert.That(client.Core.Multiplexer.CaptureMovingPublication().Endpoint.Port).IsEqualTo(target.Port);
            await Assert.That(async () => await pool.RetireAsync()).ThrowsExactly<InvalidOperationException>();
            logger.ThrowOnDisconnect = false;
            await client.DisposeAsync();
            await Assert.That(logger.PoolDisposalWarnings).IsEqualTo(1);
        }
        finally { logger.ThrowOnDisconnect = false; }
    }

    private static FakeRespServer MaintenanceServer() => new(4, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command == "HELLO 3"
            ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply,
    };

    [Test]
    public async Task DisposalVisitsEveryPoolWhenOneCleanupThrows()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        var logger = new CleanupFailureLogger();
        await using var failed = CreatePool(server, logger);
        await using var healthy = CreatePool(server);
        var ledger = new DedicatedPoolLedger(new());
        ledger.Add(failed);
        ledger.Add(healthy);
        var failedLease = await failed.RentAsync(CancellationToken.None);
        var healthyLease = await healthy.RentAsync(CancellationToken.None);
        failed.Return(failedLease);
        await Assert.That(async () => await ledger.RetireAsync(failed)).ThrowsExactly<InvalidOperationException>();
        logger.ThrowOnWarning = true;
        try
        {
            await Assert.That(async () => await ledger.DisposeAllAsync().WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
            await Assert.That(healthyLease.IsConnected).IsFalse();
            await Assert.That(ledger.Count).IsEqualTo(1);
        }
        finally { logger.ThrowOnWarning = false; }
        await ledger.DisposeAllAsync().WaitAsync(Limit);
        await Assert.That(ledger.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BulkRetirementPrunesCompletedPoolsWhileKeepingBorrowedPools()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var borrowedPool = CreatePool(server);
        await using var idlePool = CreatePool(server);
        var ledger = new DedicatedPoolLedger(new());
        ledger.Add(borrowedPool);
        ledger.Add(idlePool);
        var borrowed = await borrowedPool.RentAsync(CancellationToken.None);
        var idle = await idlePool.RentAsync(CancellationToken.None);
        idlePool.Return(idle);
        var retirement = ledger.RetireAllAsync();
        await ledger.RetireAsync(idlePool).WaitAsync(Limit);
        await Assert.That(ledger.Count).IsEqualTo(1);
        await Assert.That(retirement.IsCompleted).IsFalse();
        borrowedPool.Return(borrowed);
        await retirement.WaitAsync(Limit);
        await Assert.That(ledger.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ClientDisposalContinuesAfterMainPoolCleanupFails()
    {
        await using var server = new FakeRespServer(3)
        {
            ReplyOverride = (_, command) => command == "SUBSCRIBE ch"
                ? "*3\r\n$9\r\nsubscribe\r\n$2\r\nch\r\n:1\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
        var logger = new CleanupFailureLogger();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)], LoggerFactory = logger,
        });
        var core = client.Core;
        var connection = core.Multiplexer.GetConnection();
        var hub = core.Hub;
        await using var subscription = await client.SubscribeAsync("ch");
        var pool = core.DedicatedPool;
        var lease = await pool.RentAsync(CancellationToken.None);
        pool.Return(lease);
        await Assert.That(async () => await pool.RetireAsync()).ThrowsExactly<InvalidOperationException>();
        logger.ThrowOnDisconnect = false;
        logger.ThrowOnWarning = true;
        try
        {
            await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(core.Multiplexer.IsRetired).IsTrue();
            await Assert.That(connection.IsConnected).IsFalse();
            await Assert.That(subscription.Completion.IsCompleted).IsTrue();
            await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.ClientDisposed);
        }
        finally
        {
            logger.ThrowOnWarning = false;
            // Also close owners explicitly when the regression is run against the broken implementation.
            await hub.DisposeAsync();
            await core.Multiplexer.DisposeAsync();
            await pool.DisposeAsync();
        }
    }

    private sealed class CleanupFailureLogger : ILogger, ILoggerFactory
    {
        internal readonly InvalidOperationException Failure = new("Injected pool cleanup failure.");
        internal bool ThrowOnWarning;
        internal bool ThrowOnDisconnect = true;
        internal int Warnings;
        internal int PoolDisposalWarnings;
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnDisconnect && logLevel == LogLevel.Debug && formatter(state, exception).StartsWith("Disconnected from", StringComparison.Ordinal))
                throw Failure;
            if (logLevel != LogLevel.Warning) return;
            if (formatter(state, exception).StartsWith("Failed to dispose a dedicated pool", StringComparison.Ordinal))
                Interlocked.Increment(ref PoolDisposalWarnings);
            Interlocked.Increment(ref Warnings);
            if (ThrowOnWarning) throw Failure;
        }
    }

    private static DedicatedConnectionPool CreatePool(FakeRespServer server, ILogger? logger = null)
        => new("127.0.0.1", server.Port, RespireConnectionOptions.Default, logger);
}
