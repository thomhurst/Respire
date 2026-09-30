using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeTransactionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task QueuedWritesAndOwnedArgumentsWaitForExec(int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Protocol = (RespProtocol)protocol };
        await using var observer = await RespireClient.ConnectAsync(options);
        await using var session = await TestRespSession.ConnectAsync(options);
        using (var multi = await session.CommandAsync("MULTI"))
            await Assert.That(multi.AsString()).IsEqualTo("OK");
        byte[] value = [0, 255, 128];
        using (var queued = await session.CommandBytesAsync("SET"u8.ToArray(), "key"u8.ToArray(), value))
            await Assert.That(queued.AsString()).IsEqualTo("QUEUED");
        value[0] = 9;
        using (var overwritten = await session.CommandAsync("ECHO", new string('x', 8192)))
            await Assert.That(overwritten.AsString()).IsEqualTo("QUEUED");
        await Assert.That(await observer.ExistsAsync("key")).IsFalse();
        using var executed = await session.CommandAsync("EXEC");
        await Assert.That(executed.AsArray().Length).IsEqualTo(2);
        await Assert.That(executed.AsArray()[0].AsString()).IsEqualTo("OK");
        await Assert.That(executed.AsArray()[1].AsString().Length).IsEqualTo(8192);
        await Assert.That((await observer.GetBytesAsync("key"))!.AsSpan().SequenceEqual(new byte[] { 0, 255, 128 })).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExecDisconnectSeparatesAcceptanceWithoutReplay(bool afterExecution)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var observer = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(afterExecution));
        await using var transaction = client.CreateTransaction();
        transaction.Increment("counter");
        transaction.Increment("counter");
        await Assert.That(async () => await transaction.CommitAsync().AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(fault.ExecutionCount).IsEqualTo(afterExecution ? 1 : 0);
        await Assert.That(await observer.GetStringAsync("counter")).IsEqualTo(afterExecution ? "2" : null);
    }

    [Test]
    public async Task WatchExpiryIsCheckedAtExecWithoutAnInterveningRead()
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.SetAsync("expiring", "old", RespireExpiry.In(TimeSpan.FromSeconds(1)));
        await using var transaction = await client.CreateTransactionAsync(["expiring"]);
        transaction.Set("result", "must not execute");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await transaction.CommitAsync()).IsFalse();
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
        // An already expired key is logically missing when WATCH starts.
        await using var next = await client.CreateTransactionAsync(["expiring"]);
        next.Set("result", "committed");
        await Assert.That(await next.CommitAsync()).IsTrue();
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 3)]
    public async Task CancelledCommitDrainsExecWithoutUndoOrReplay(bool afterExecution, int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Connections = 1, Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        await using var observer = await RespireClient.ConnectAsync(options);
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Pause(gate, afterExecution));
        using var cancellation = new CancellationTokenSource();
        await using var transaction = client.CreateTransaction();
        var first = transaction.Increment("counter");
        transaction.Increment("counter");
        var commit = transaction.CommitAsync(cancellation.Token).AsTask();
        await fault.Matched.WaitAsync(Limit);
        await Assert.That(await observer.GetStringAsync("counter")).IsEqualTo(afterExecution ? "2" : null);
        cancellation.Cancel();
        await Assert.That(async () => await commit.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(first.Status).IsEqualTo(RespirePendingStatus.Faulted);
        gate.Release();
        // The next reply on the same connection proves EXEC drained in FIFO order.
        await Assert.That(await client.GetStringAsync("counter").AsTask().WaitAsync(Limit)).IsEqualTo("2");
        await Assert.That(fault.ExecutionCount).IsEqualTo(1);
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ShutdownClearsWatchedQueuesAndJoinsHeldExec(bool disposeServer, bool afterExecution)
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using (var watched = await session.CommandAsync("WATCH", "key")) { }
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("SET", "key", "value")) { }
        var loops = ConnectionLoops(server);
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Pause(new(), afterExecution));
        var commit = session.CommandAsync("EXEC");
        await fault.Matched.WaitAsync(Limit);
        await (disposeServer ? server.DisposeAsync().AsTask() : session.DisposeAsync().AsTask()).WaitAsync(Limit);
        await Assert.That(async () => { using var reply = await commit.WaitAsync(Limit); }).Throws<Exception>();
        await Task.WhenAll(loops).WaitAsync(Limit);
        await Assert.That(WatcherCount(server)).IsEqualTo(0);
        await Assert.That(fault.ExecutionCount).IsEqualTo(afterExecution ? 1 : 0);
        if (!disposeServer)
        {
            await using var observer = await RespireClient.ConnectAsync(server.CreateOptions());
            await Assert.That(await observer.GetStringAsync("key")).IsEqualTo(afterExecution ? "value" : null);
        }
    }

    [Test]
    [Arguments("SET")]
    [Arguments("EXEC")]
    public async Task RejectedWireCommandAbortsTransactionAndClearsState(string command)
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault(command, RespireFakeFault.ReadOnly());
        using (var watched = await session.CommandAsync("WATCH", "key")) { }
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("SET", "key", "value"))
            await Assert.That(queued.IsError).IsEqualTo(command == "SET");
        using (var aborted = await session.CommandAsync("EXEC"))
            await Assert.That(aborted.GetErrorMessage()).StartsWith("EXECABORT");
        await Assert.That(WatcherCount(server)).IsEqualTo(0);
        using (var absent = await session.CommandAsync("GET", "key")) await Assert.That(absent.IsNull).IsTrue();
        using (var next = await session.CommandAsync("MULTI")) { }
        using (var empty = await session.CommandAsync("EXEC")) await Assert.That(empty.AsArray().Length).IsEqualTo(0);
    }

    [Test]
    public async Task ExecUsesOneClockSampleAndDoesNotRematchQueuedFaults()
    {
        var clock = new AdvancingClock();
        await using var server = new RespireFakeServer(clock);
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("SET", "key", "value", "PX", "1")) { }
        using (var queued = await session.CommandAsync("PTTL", "key")) { }
        using (var queued = await session.CommandAsync("GET", "key")) { }
        // Installed after queueing: internal EXEC traversal must not match received-command rules.
        using var fault = server.InjectFault("SET", RespireFakeFault.ReadOnly());
        var samples = clock.Samples;
        using var executed = await session.CommandAsync("EXEC");
        await Assert.That(clock.Samples - samples).IsEqualTo(1);
        await Assert.That(executed.AsArray()[0].AsString()).IsEqualTo("OK");
        await Assert.That(executed.AsArray()[1].AsInteger()).IsEqualTo(1);
        await Assert.That(executed.AsArray()[2].AsString()).IsEqualTo("value");
        await Assert.That(fault.MatchedCount).IsEqualTo(0);
    }

    [Test]
    public async Task QueueLimitRejectsWithoutMutationAndAllowsNextTransaction()
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using (var multi = await session.CommandAsync("MULTI")) { }
        var payload = new byte[1024 * 1024];
        for (var index = 0; index < 15; index++)
        {
            using var queued = await session.CommandBytesAsync("SET"u8.ToArray(), "key"u8.ToArray(), payload);
            await Assert.That(queued.AsString()).IsEqualTo("QUEUED");
        }
        using (var rejected = await session.CommandBytesAsync("SET"u8.ToArray(), "key"u8.ToArray(), payload))
            await Assert.That(rejected.GetErrorMessage()).Contains("transaction queue exceeds 16 MiB");
        using (var aborted = await session.CommandAsync("EXEC"))
            await Assert.That(aborted.GetErrorMessage()).StartsWith("EXECABORT");
        using (var absent = await session.CommandAsync("GET", "key")) await Assert.That(absent.IsNull).IsTrue();
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("SET", "key", "small")) { }
        using (var executed = await session.CommandAsync("EXEC"))
            await Assert.That(executed.AsArray()[0].AsString()).IsEqualTo("OK");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WatchOwnsBinaryKeysAndDisconnectDiscardsQueuedWrites(int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Protocol = (RespProtocol)protocol };
        await using var session = await TestRespSession.ConnectAsync(options);
        byte[] key = [0, 255, 128];
        using (var watched = await session.CommandBytesAsync("WATCH"u8.ToArray(), key, key)) { }
        key[0] = 9;
        using (var overwritten = await session.CommandAsync("ECHO", new string('x', 8192))) { }
        await using (var writer = await TestRespSession.ConnectAsync(options))
        {
            using var changed = await writer.CommandBytesAsync("SET"u8.ToArray(), [0, 255, 128], "changed"u8.ToArray());
        }
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("SET", "discarded", "value")) { }
        using (var aborted = await session.CommandAsync("EXEC")) await Assert.That(aborted.IsNull).IsTrue();
        using (var watched = await session.CommandAsync("WATCH", "watched")) { }
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("SET", "discarded", "value")) { }
        var loops = ConnectionLoops(server);
        await session.DisposeAsync();
        await Task.WhenAll(loops).WaitAsync(Limit);
        await Assert.That(WatcherCount(server)).IsEqualTo(0);
        await using var observer = await RespireClient.ConnectAsync(options);
        await Assert.That(await observer.ExistsAsync("discarded")).IsFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnsupportedCommandsAndProtocolChangesInvalidateQueue(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        foreach (var arguments in new[] { new[] { "HELLO", "3" }, new[] { "SUBSCRIBE", "channel" }, new[] { "EVAL", "return 1", "0" } })
        {
            using (var multi = await session.CommandAsync("MULTI")) { }
            using (var rejected = await session.CommandAsync(arguments)) await Assert.That(rejected.IsError).IsTrue();
            using (var aborted = await session.CommandAsync("EXEC"))
                await Assert.That(aborted.GetErrorMessage()).StartsWith("EXECABORT");
            using var alive = await session.CommandAsync("PING");
            await Assert.That(alive.AsString()).IsEqualTo("PONG");
        }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        internal int Samples;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Interlocked.Increment(ref Samples));
    }

    private const System.Reflection.BindingFlags PrivateInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static Task[] ConnectionLoops(RespireFakeServer server)
    {
        var gate = typeof(RespireFakeServer).GetField("_gate", PrivateInstance)!.GetValue(server)!;
        lock (gate)
        {
            var connections = (System.Collections.IEnumerable)typeof(RespireFakeServer).GetField("_connections", PrivateInstance)!.GetValue(server)!;
            return connections.Cast<object>().Select(connection => (Task)connection.GetType().GetProperty("Completion", PrivateInstance)!.GetValue(connection)!).ToArray();
        }
    }

    private static int WatcherCount(RespireFakeServer server)
    {
        var gate = typeof(RespireFakeServer).GetField("_gate", PrivateInstance)!.GetValue(server)!;
        lock (gate)
            return ((System.Collections.IDictionary)typeof(RespireFakeServer).GetField("_watchers", PrivateInstance)!.GetValue(server)!).Count;
    }
}
