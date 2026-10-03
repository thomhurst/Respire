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
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task ClientDisposalContinuesAfterMainPoolCleanupFails(bool laterOwnerAlsoFails, bool aggregateFailure)
    {
        await using var server = new FakeRespServer(3)
        {
            ReplyOverride = (_, command) => command == "SUBSCRIBE ch"
                ? "*3\r\n$9\r\nsubscribe\r\n$2\r\nch\r\n:1\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
        var logger = new CleanupFailureLogger();
        if (aggregateFailure) logger.WarningFailure = new AggregateException(logger.WarningFailure);
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
        logger.ThrowOnDisconnect = laterOwnerAlsoFails;
        logger.ThrowOnWarning = true;
        try
        {
            if (laterOwnerAlsoFails)
            {
                var error = await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                    .ThrowsExactly<AggregateException>();
                var failures = error!.InnerExceptions;
                await Assert.That(failures.Any(failure => failure is AggregateException)).IsFalse();
                await Assert.That(failures.Contains(logger.WarningFailure)).IsTrue();
                await Assert.That(failures.Contains(logger.Failure)).IsTrue();
            }
            else if (aggregateFailure)
            {
                var error = await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                    .ThrowsExactly<AggregateException>();
                await Assert.That(ReferenceEquals(error, logger.WarningFailure)).IsTrue();
            }
            else
            {
                var error = await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                    .ThrowsExactly<InvalidOperationException>();
                await Assert.That(ReferenceEquals(error, logger.WarningFailure)).IsTrue();
            }
            await Assert.That(core.Multiplexer.IsRetired).IsTrue();
            await Assert.That(connection.IsConnected).IsFalse();
            await Assert.That(subscription.Completion.IsCompleted).IsTrue();
            await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.ClientDisposed);
        }
        finally
        {
            logger.ThrowOnWarning = false;
            logger.ThrowOnDisconnect = false;
            // Also close owners explicitly when the regression is run against the broken implementation.
            await hub.DisposeAsync();
            try { await core.Multiplexer.DisposeAsync(); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, logger.Failure)) { }
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task ClientDisposalPreservesEveryFailedPool()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var firstLogger = new CleanupFailureLogger();
        var secondLogger = new CleanupFailureLogger();
        await using var first = CreatePool(server, firstLogger);
        await using var second = CreatePool(server, secondLogger);
        // Inject distinct failures into two owned pools to exercise the bulk task's exception collection.
        var ledger = client.Core.OwnedPools;
        ledger.Add(first);
        ledger.Add(second);
        foreach (var pool in new[] { first, second })
        {
            var lease = await pool.RentAsync(CancellationToken.None);
            pool.Return(lease);
            await Assert.That(async () => await pool.RetireAsync()).ThrowsExactly<InvalidOperationException>();
        }
        var connection = client.Core.Multiplexer.GetConnection();
        firstLogger.ThrowOnWarning = secondLogger.ThrowOnWarning = true;
        try
        {
            var error = await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                .ThrowsExactly<AggregateException>();
            await Assert.That(error!.InnerExceptions.Count).IsEqualTo(2);
            await Assert.That(error.InnerExceptions.Contains(firstLogger.WarningFailure)).IsTrue();
            await Assert.That(error.InnerExceptions.Contains(secondLogger.WarningFailure)).IsTrue();
            await Assert.That(connection.IsConnected).IsFalse();
            await Assert.That(client.Core.Multiplexer.IsRetired).IsTrue();
        }
        finally { firstLogger.ThrowOnWarning = secondLogger.ThrowOnWarning = false; }
    }

    [Test]
    [Arguments("cluster")]
    [Arguments("replicas")]
    [Arguments("sentinel")]
    public async Task ClientDisposalPreservesFailuresInsideRouterOwners(string mode)
    {
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var third = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var sentinel = new FakeRespServer(8)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? System.Text.Encoding.ASCII.GetBytes($"*2\r\n+127.0.0.1\r\n+{primary.Port}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? System.Text.Encoding.ASCII.GetBytes($"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n+127.0.0.1\r\n:{primary.Port}\r\n*3\r\n:8192\r\n:16383\r\n*2\r\n+127.0.0.1\r\n:{second.Port}\r\n")
            : command == "ROLE" ? "*3\r\n+master\r\n:0\r\n*0\r\n"u8.ToArray() : FakeRespServer.OkReply;
        foreach (var replica in new[] { second, third })
            replica.ReplyOverride = (_, command) => command == "ROLE"
                ? "*5\r\n+slave\r\n+127.0.0.1\r\n:6379\r\n+connected\r\n:0\r\n"u8.ToArray() : FakeRespServer.OkReply;
        var logger = new CleanupFailureLogger { ThrowOnDisconnect = false, DistinctDisconnectFailures = true };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2,
            Endpoints = [new("127.0.0.1", mode == "sentinel" ? sentinel.Port : primary.Port)],
            UseCluster = mode == "cluster", ClusterTopologyRefreshInterval = null,
            SentinelPrimaryName = mode == "sentinel" ? "primary" : null,
            ReplicaEndpoints = mode == "replicas" ? [new("127.0.0.1", second.Port), new("127.0.0.1", third.Port)] : [],
            LoggerFactory = logger,
        });
        if (mode == "cluster")
            await client.Core.Cluster!.GetConnectionAsync(10000, CancellationToken.None, discovery: null);
        if (mode == "replicas")
            foreach (var replica in new[] { second, third })
                await client.Core.ReadRouter.GetReplicaFromEndpointsAsync([new("127.0.0.1", replica.Port)], CancellationToken.None);
        logger.ThrowOnDisconnect = true;
        try
        {
            var error = await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                .ThrowsExactly<AggregateException>();
            await Assert.That(logger.DisconnectFailures.Count).IsGreaterThanOrEqualTo(mode == "replicas" ? 6 : mode == "cluster" ? 4 : 2);
            foreach (var failure in logger.DisconnectFailures)
                await Assert.That(error!.InnerExceptions.Contains(failure)).IsTrue();
            await Assert.That(error!.InnerExceptions.Any(failure => failure is AggregateException)).IsFalse();
        }
        finally { logger.ThrowOnDisconnect = false; }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClientDisposalPreservesEveryFailedClusterRetirement(bool abortAlsoFails)
    {
        await using var seed = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var first = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var replacement = new FakeRespServer(8, FakeRespServer.OkReply);
        var logger = new CleanupFailureLogger { ThrowOnDisconnect = false, DistinctDisconnectFailures = true };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)], LoggerFactory = logger,
        });
        var router = client.Core.Cluster!;
        router.ApplyTopology([
            new(0, 8191, new("127.0.0.1", first.Port), "first", []),
            new(8192, 16383, new("127.0.0.1", second.Port), "second", []),
        ], 0, 1);
        foreach (var server in new[] { first, second })
        {
            var pool = router.GetDedicatedPool(new("127.0.0.1", server.Port));
            var lease = await pool.RentAsync(CancellationToken.None);
            pool.Return(lease);
        }
        logger.ThrowOnDisconnect = true;
        router.ApplyTopology([new(0, 16383, new("127.0.0.1", replacement.Port), "replacement", [])], 0, 2);
        try { await router.WaitForRetirementAsync().WaitAsync(Limit); }
        catch (InvalidOperationException) { }
        logger.ThrowOnDisconnect = false;
        await Assert.That(logger.DisconnectFailures.Count).IsEqualTo(2);
        if (abortAlsoFails)
            await router.GetConnectionAsync(0, CancellationToken.None, discovery: null);
        logger.ThrowOnDisconnect = abortAlsoFails;
        try
        {
            var error = await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                .ThrowsExactly<AggregateException>();
            await Assert.That(error!.InnerExceptions.Count).IsEqualTo(abortAlsoFails ? 3 : 2);
            foreach (var failure in logger.DisconnectFailures)
                await Assert.That(error.InnerExceptions.Contains(failure)).IsTrue();
        }
        finally { logger.ThrowOnDisconnect = false; }
    }

    private sealed class CleanupFailureLogger : ILogger, ILoggerFactory
    {
        internal readonly InvalidOperationException Failure = new("Injected pool cleanup failure.");
        internal Exception WarningFailure = new InvalidOperationException("Injected pool warning failure.");
        internal readonly System.Collections.Concurrent.ConcurrentBag<Exception> DisconnectFailures = [];
        internal bool DistinctDisconnectFailures;
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
            {
                if (!DistinctDisconnectFailures) throw Failure;
                var failure = new InvalidOperationException("Injected distinct disconnect failure.");
                DisconnectFailures.Add(failure);
                throw failure;
            }
            if (logLevel != LogLevel.Warning) return;
            if (formatter(state, exception).StartsWith("Failed to dispose a dedicated pool", StringComparison.Ordinal))
                Interlocked.Increment(ref PoolDisposalWarnings);
            Interlocked.Increment(ref Warnings);
            if (ThrowOnWarning) throw WarningFailure;
        }
    }

    private static DedicatedConnectionPool CreatePool(FakeRespServer server, ILogger? logger = null)
        => new("127.0.0.1", server.Port, RespireConnectionOptions.Default, logger);
}
