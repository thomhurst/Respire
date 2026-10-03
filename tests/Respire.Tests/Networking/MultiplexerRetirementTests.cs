using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class MultiplexerRetirementTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false, 1, false)]
    [Arguments(false, 3, false)]
    [Arguments(false, 3, true)]
    [Arguments(true, 1, false)]
    [Arguments(true, 3, false)]
    [Arguments(true, 3, true)]
    public async Task RetirementPreservesPhysicalFailures(bool maintenance, int failures, bool distinct)
    {
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
        var logger = new DisconnectFailureLogger(failures, distinct);
        var options = new RespireOptions
        {
            Protocol = maintenance ? RespProtocol.Resp3 : RespProtocol.Resp2,
            MaintenanceNotifications = maintenance
                ? RespireMaintenanceNotificationMode.Enabled : RespireMaintenanceNotificationMode.Disabled,
        };
        var node = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            connectionCount: 3, options: options.ToConnectionOptions(enableMaintenanceNotifications: maintenance), logger: logger);
        var connections = Enumerable.Range(0, 3).Select(node.GetConnection).ToArray();
        try
        {
            var retirement = node.RetireAsync();
            var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).Throws<Exception>();
            await Assert.That(logger.Disconnects).IsEqualTo(3);
            await Assert.That(logger.Failures.Count).IsEqualTo(failures);
            if (distinct)
            {
                await Assert.That(error).IsTypeOf<AggregateException>();
                var aggregate = (AggregateException)error!;
                await Assert.That(aggregate.InnerExceptions.Count).IsEqualTo(failures);
                foreach (var failure in logger.Failures)
                    await Assert.That(aggregate.InnerExceptions.Contains(failure)).IsTrue();
            }
            else await Assert.That(ReferenceEquals(error, logger.SharedFailure)).IsTrue();

            await Assert.That(ReferenceEquals(retirement, node.RetireAsync())).IsTrue();
            foreach (var connection in connections)
            {
                await Assert.That(connection.IsAcceptingCommands).IsFalse();
                await Assert.That(connection.IsConnected).IsFalse();
            }
            if (maintenance)
                await Assert.That(server.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(3);
        }
        finally
        {
            // Disposal re-observes the same failed connection cleanup tasks.
            try { await node.DisposeAsync().AsTask().WaitAsync(Limit); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, logger.SharedFailure)) { }
            catch (AggregateException error) when (error.InnerExceptions.All(logger.Failures.Contains)) { }
        }
    }

    private sealed class DisconnectFailureLogger(int failures, bool distinct) : ILogger
    {
        internal readonly InvalidOperationException SharedFailure = new("Injected disconnect failure.");
        internal readonly ConcurrentBag<Exception> Failures = [];
        internal int Disconnects;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Debug || !formatter(state, exception).StartsWith("Disconnected from", StringComparison.Ordinal))
                return;
            if (Interlocked.Increment(ref Disconnects) > failures) return;
            var failure = distinct ? new InvalidOperationException("Injected distinct disconnect failure.") : SharedFailure;
            Failures.Add(failure);
            throw failure;
        }
    }
}
