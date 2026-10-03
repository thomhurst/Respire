using System.Collections.Concurrent;
using System.Net.Sockets;
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
        var sharedFailure = new InvalidOperationException("Injected disconnect failure.");
        ConcurrentBag<Exception> injectedFailures = [];
        ConcurrentBag<FailingDisposeStream> streams = [];
        var created = 0;
        var options = new RespireOptions
        {
            Protocol = maintenance ? RespProtocol.Resp3 : RespProtocol.Resp2,
            MaintenanceNotifications = maintenance
                ? RespireMaintenanceNotificationMode.Enabled : RespireMaintenanceNotificationMode.Disabled,
        };
        var connectionOptions = options.ToConnectionOptions(enableMaintenanceNotifications: maintenance) with
        {
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    var index = Interlocked.Increment(ref created);
                    Exception? failure = null;
                    if (index <= failures)
                        failure = distinct ? new InvalidOperationException("Injected distinct disconnect failure.") : sharedFailure;
                    var stream = new FailingDisposeStream(socket, failure, injectedFailures);
                    streams.Add(stream);
                    return stream;
                }
                catch { socket.Dispose(); throw; }
            },
        };
        var node = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            connectionCount: 3, options: connectionOptions);
        var connections = Enumerable.Range(0, 3).Select(node.GetConnection).ToArray();
        try
        {
            var retirement = node.RetireAsync();
            var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).Throws<Exception>();
            await Assert.That(streams.Count).IsEqualTo(3);
            await Assert.That(streams.All(stream => stream.DisposeCalls > 0)).IsTrue();
            await Assert.That(injectedFailures.Count).IsEqualTo(failures);
            if (distinct)
            {
                await Assert.That(error).IsTypeOf<AggregateException>();
                var aggregate = (AggregateException)error!;
                await Assert.That(aggregate.InnerExceptions.Count).IsEqualTo(failures);
                foreach (var failure in injectedFailures)
                    await Assert.That(aggregate.InnerExceptions.Contains(failure)).IsTrue();
            }
            else await Assert.That(ReferenceEquals(error, sharedFailure)).IsTrue();

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
            catch (InvalidOperationException error) when (ReferenceEquals(error, sharedFailure)) { }
            catch (AggregateException error) when (error.InnerExceptions.All(injectedFailures.Contains)) { }
        }
    }

    private sealed class FailingDisposeStream(Socket socket, Exception? failure, ConcurrentBag<Exception> failures)
        : NetworkStream(socket, ownsSocket: true)
    {
        internal int DisposeCalls;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            var call = Interlocked.Increment(ref DisposeCalls);
            if (failure is null) return;
            if (call == 1) failures.Add(failure);
            // Abort may suppress the first disposal failure. Final cleanup must observe
            // the same failure again, while each physical stream is counted only once.
            throw failure;
        }
    }
}
