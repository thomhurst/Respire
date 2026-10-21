using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

// Lifetime edges of whole-unit batch and transaction retries: client disposal while a unit
// waits to resend, failures while publishing the final attempt, and independent commands
// that share the connection with a pending unit.
[NotInParallel]
public partial class UnitRetryLifetimeTests
{
    private static readonly byte[] TryAgain = "-TRYAGAIN busy\r\n"u8.ToArray();
    private static readonly byte[] Value = "$5\r\nvalue\r\n"u8.ToArray();

    [Test]
    [Arguments("standalone", false, "backoff")]
    [Arguments("standalone", true, "backoff")]
    [Arguments("standalone", false, "replacement")]
    [Arguments("standalone", true, "replacement")]
    [Arguments("sentinel", false, "backoff")]
    [Arguments("sentinel", true, "backoff")]
    [Arguments("sentinel", false, "replacement")]
    [Arguments("sentinel", true, "replacement")]
    public async Task DisposingClientDuringUnitRetryWaitFaultsUnitWithoutResend(string topology, bool transaction, string phase)
    {
        var attempts = 0;
        byte[]? Fail(FakeRespServer server)
        {
            Interlocked.Increment(ref attempts);
            if (phase == "backoff") return transaction ? "*1\r\n-TRYAGAIN busy\r\n"u8.ToArray() : TryAgain;
            // The unit may have executed. A read-only unit still permits the resend, which
            // must wait for a replacement connection that the stream factory holds back.
            server.CloseConnections();
            return null;
        }
        await using var primary = UnitServer((command, server) => command == "GET key" ? Fail(server) : null, Fail);
        await using var sentinel = topology == "sentinel" ? Sentinel(primary.Port) : null;
        var client = await RespireClient.ConnectAsync(Options(sentinel ?? primary, sentinel is not null) with
        {
            CommandTimeout = TimeSpan.FromSeconds(30),
            TestingStreamFactory = async (host, port, token) =>
            {
                // Hold every replacement dial to the primary until the client cancels it.
                if (port == primary.Port && Volatile.Read(ref attempts) != 0) await Task.Delay(Timeout.Infinite, token);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
            RetryPolicy = phase == "backoff"
                ? new() { InitialBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(10), Jitter = 0 }
                : new() { InitialBackoff = TimeSpan.Zero },
        });
        Task<(string? Value, Exception? Error)> running;
        try
        {
            running = ReadUnitAsync(client, transaction);
            await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1);
            // Let the retry owner classify the failure and start waiting.
            await Task.Delay(250);
            await Assert.That(running.IsCompleted).IsFalse();
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        var disposing = client.DisposeAsync().AsTask();
        var (value, error) = await running.WaitAsync(TimeSpan.FromSeconds(5));
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(value).IsNull();
        // Disposal ends the backoff or replacement wait at once. It reports disposal, not an
        // internal cancellation that the caller never requested.
        await Assert.That(error).IsAssignableTo<ObjectDisposedException>();
        await Task.Delay(100);
        await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConversionFailureOnFinalAttemptFaultsOnlyThatMemberAndReleasesFence(bool transaction)
    {
        var attempts = 0;
        await using var server = UnitServer((command, _) => command switch
        {
            // The abandoned attempt converts cleanly. The final attempt does not.
            "GET number" => Volatile.Read(ref attempts) == 0 ? "$2\r\n42\r\n"u8.ToArray() : "$3\r\nbad\r\n"u8.ToArray(),
            "GET key" => Interlocked.Increment(ref attempts) == 1 ? TryAgain : Value,
            _ => null,
        }, _ => Interlocked.Increment(ref attempts) == 1
            ? "*3\r\n+OK\r\n$2\r\n42\r\n-TRYAGAIN busy\r\n"u8.ToArray()
            : "*3\r\n+OK\r\n$3\r\nbad\r\n$5\r\nvalue\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates final-attempt conversion failure and release of the unit mutation fence.
        await using var client = await RespireClient.ConnectAsync(Options(server, sentinel: false) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(),
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins },
        });
#pragma warning restore RESP010
        Exception? numberError;
        if (transaction)
        {
            await using var unit = client.CreateTransaction();
            var write = unit.Set("written", "v");
            var number = unit.Get<int>("number");
            var read = unit.GetString("key");
            // A member conversion failure is not a transport or commit failure.
            await unit.CommitAsync();
            await Assert.That(await write).IsTrue();
            await Assert.That(await read).IsEqualTo("value");
            numberError = await Failure(async () => await number);
        }
        else
        {
            using var batch = client.CreateBatch();
            var write = batch.Set("written", "v");
            var number = batch.Get<int>("number");
            var read = batch.GetString("key");
            var result = await batch.TryExecuteAsync();
            await Assert.That(result.FailureCount).IsEqualTo(1);
            await Assert.That(write.Result).IsTrue();
            await Assert.That(read.Result).IsEqualTo("value");
            await Assert.That(number.HasResult).IsFalse();
            numberError = number.Error;
        }
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(numberError).IsNotNull();
        await Assert.That(numberError is RespireServerException or RespireConnectionException).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command == "SET written v")).IsEqualTo(2);
        // The cache mutation fence spans both attempts and is released despite the failure.
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        // Discarded and failed replies leave the shared connection's FIFO intact.
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IndependentCommandsKeepFifoResultsAroundPendingUnitRetry(bool transaction)
    {
        const int independent = 16;
        var clock = new ClusterRecoveryTestClock();
        var backoff = TimeSpan.FromSeconds(2);
        var replyHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var server = UnitServer((command, _) =>
        {
            if (command == "GET key") return Interlocked.Increment(ref attempts) == 1 ? TryAgain : Value;
            return command.StartsWith("GET i", StringComparison.Ordinal) ? Bulk("v" + command[5..]) : null;
        }, _ => Interlocked.Increment(ref attempts) == 1 ? "*1\r\n-TRYAGAIN busy\r\n"u8.ToArray() : "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client keeps the retry unit and independent commands on one connection to verify FIFO ownership.
        await using var client = await RespireClient.ConnectAsync(Options(server, sentinel: false) with
        {
            // This test controls progress at each phase; a shared wall-clock command deadline
            // must not expire while the harness deliberately holds a reply. Bound each await instead.
            CommandTimeout = null,
            RetryPolicy = new() { InitialBackoff = backoff, MaxBackoff = backoff, Jitter = 0 },
        });
#pragma warning restore RESP010
        client.Core.UnitRetryClock = clock;
        var target = transaction ? "EXEC" : "GET key";
        var running = ReadUnitAsync(client, transaction);
        try
        {
            // Timer registration proves that the first failed attempt has been consumed and
            // the unit is waiting in backoff. It cannot resend until this test advances the clock.
            await WaitUntilAsync(() => clock.ActiveTimers == 1);
            var during = await Task.WhenAll(Enumerable.Range(0, independent)
                .Select(index => client.GetStringAsync($"i{index}").AsTask())).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(running.IsCompleted).IsFalse();
            await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(1);
            for (var index = 0; index < independent; index++) await Assert.That(during[index]).IsEqualTo($"v{index}");

            // Install the reply gate before allowing the resend. Its arrival callback proves
            // the retry is on the wire, independently of continuation scheduling speed.
            server.GateCommand(target, release.Task, () => replyHeld.TrySetResult());
            clock.Advance(backoff);
            await replyHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var behind = Enumerable.Range(independent, independent)
                .Select(index => client.GetStringAsync($"i{index}").AsTask()).ToArray();
            var connection = client.Core.Multiplexer.GetConnection();
            // Observe actual FIFO admission instead of sleeping for a negative-completion probe.
            await WaitUntilAsync(() => connection.PendingResponseCount >= independent + 1);
            await Assert.That(running.IsCompleted).IsFalse();
            await Assert.That(behind.Any(task => task.IsCompleted)).IsFalse();
            release.TrySetResult();
            var (value, error) = await running.WaitAsync(TimeSpan.FromSeconds(5));
            var after = await Task.WhenAll(behind).WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(error).IsNull();
            await Assert.That(value).IsEqualTo("value");
            for (var index = 0; index < independent; index++) await Assert.That(after[index]).IsEqualTo($"v{index + independent}");
            await Assert.That(attempts).IsEqualTo(2);
            // Each independent command was sent once, and none was swallowed by the unit attempts.
            var commands = server.ReceivedCommands.ToArray();
            var first = Array.IndexOf(commands, target);
            var second = Array.LastIndexOf(commands, target);
            for (var index = 0; index < independent * 2; index++)
            {
                var position = Array.IndexOf(commands, $"GET i{index}");
                await Assert.That(commands.Count(command => command == $"GET i{index}")).IsEqualTo(1);
                await Assert.That(index < independent ? position > first && position < second : position > second).IsTrue();
            }
        }
        finally
        {
            release.TrySetResult();
            // Disposal cancels an unadvanced backoff too, including failures before timer registration.
            await client.DisposeAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<(string? Value, Exception? Error)> ReadUnitAsync(IRespireClient client, bool transaction)
    {
        if (transaction)
        {
            await using var unit = client.CreateTransaction();
            var read = unit.GetString("key");
            try { await unit.CommitAsync(); }
            catch (Exception error)
            {
                // Every member reports the commit's final failure.
                var member = await Failure(async () => await read);
                await Assert.That(member.GetType()).IsEqualTo(error.GetType());
                return (null, error);
            }
            return (await read, null);
        }
        using var batch = client.CreateBatch();
        var value = batch.GetString("key");
        var result = await batch.TryExecuteAsync();
        return result.FailureCount == 0 ? (value.Result, null) : (null, value.Error);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private static byte[] Bulk(string value) => Encoding.ASCII.GetBytes($"${value.Length}\r\n{value}\r\n");

    // Tracks MULTI state per connection. Transaction members reply QUEUED, and EXEC replies
    // come from exec. A null reply leaves the command unanswered.
    private static FakeRespServer UnitServer(Func<string, FakeRespServer, byte[]?> reply, Func<FakeRespServer, byte[]?> exec)
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        var transactions = new ConcurrentDictionary<int, bool>();
        server.ReplyOverride = (connection, command) =>
        {
            if (command == "CLIENT ID") return ":1\r\n"u8.ToArray();
            if (command.StartsWith("HELLO", StringComparison.Ordinal)) return "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
            if (command == "ROLE") return "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();
            if (command == "MULTI") { transactions[connection] = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { transactions[connection] = false; return exec(server); }
            if (transactions.GetValueOrDefault(connection)) return "+QUEUED\r\n"u8.ToArray();
            return reply(command, server);
        };
        return server;
    }

    private static FakeRespServer Sentinel(int primaryPort) => new(16, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Encoding.ASCII.GetBytes(
                $"*2\r\n$9\r\n127.0.0.1\r\n${primaryPort.ToString().Length}\r\n{primaryPort}\r\n"),
            "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
            _ => null,
        },
    };

    private static RespireOptions Options(FakeRespServer server, bool sentinel) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], SentinelPrimaryName = sentinel ? "mymaster" : null,
        Protocol = RespireProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
        CommandTimeout = TimeSpan.FromSeconds(5), RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };
}
