using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using Respire.Infrastructure;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

// MULTI/EXEC retries resend the complete retained frame, never a subset of its members.
[NotInParallel]
public class TransactionCommandRetryTests
{
    [Test]
    public async Task LostExecReplyResendsReadOnlyUnitAndPublishesOnce()
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*2\r\n$5\r\nvalue\r\n:1\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var value = transaction.GetString("key");
        var exists = transaction.Exists("key");
        await transaction.CommitAsync();

        await Assert.That(value.Result).IsEqualTo("value");
        await Assert.That(exists.Result).IsTrue();
        await Assert.That(execs).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(command => command == "MULTI")).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
    }

    [Test]
    public async Task CircuitAwareUnitRetryWaitsForReplacementWithoutSpendingAttempts()
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CircuitBreaker = new(),
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var value = transaction.GetString("key");
        await transaction.CommitAsync();

        await Assert.That(value.Result).IsEqualTo("value");
        await Assert.That(execs).IsEqualTo(2);
    }

    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, 1)]
    [Arguments(RespireCommandRetryCategory.WriteLastWins, 1)]
    [Arguments(RespireCommandRetryCategory.WriteAccumulating, 2)]
    public async Task LostExecReplyRepeatsAccumulatingUnitOnlyWithExplicitRisk(RespireCommandRetryCategory maximum, int expectedExecs)
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*2\r\n$5\r\nvalue\r\n:2\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum } });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var value = transaction.GetString("key");
        var counter = transaction.Increment("counter");

        if (expectedExecs == 1)
        {
            // One accumulating member blocks the whole unit, including the read that is safe alone.
            await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
            await Assert.That(() => value.Result).Throws<RespireConnectionException>();
            await Assert.That(() => counter.Result).Throws<RespireConnectionException>();
        }
        else
        {
            await transaction.CommitAsync();
            await Assert.That(value.Result).IsEqualTo("value");
            await Assert.That(counter.Result).IsEqualTo(2L);
        }
        await Assert.That(execs).IsEqualTo(expectedExecs);
    }

    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, 1)]
    [Arguments(RespireCommandRetryCategory.WriteLastWins, 2)]
    public async Task OverwriteUnitRequiresOverwritePermission(RespireCommandRetryCategory maximum, int expectedExecs)
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*1\r\n+OK\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum } });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var set = transaction.Set("key", "value");

        if (expectedExecs == 1) await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
        else
        {
            await transaction.CommitAsync();
            await Assert.That(set.Result).IsTrue();
        }
        await Assert.That(execs).IsEqualTo(expectedExecs);
    }

    [Test]
    public async Task SelectedQueueErrorResendsUnitAfterExecAbort()
    {
        var gets = 0;
        await using var server = Server(
            exec: _ => Volatile.Read(ref gets) == 1 ? "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray()
                : "*1\r\n$5\r\nvalue\r\n"u8.ToArray(),
            queued: command => command == "GET key" && Interlocked.Increment(ref gets) == 1
                ? "-LOADING Redis is loading the dataset in memory\r\n"u8.ToArray() : null);
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var value = transaction.GetString("key");
        await transaction.CommitAsync();

        await Assert.That(value.Result).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command == "EXEC")).IsEqualTo(2);
    }

    [Test]
    [Arguments("READONLY", RespireCommandRetryCategory.ReadOnly, 1)]
    [Arguments("READONLY", RespireCommandRetryCategory.WriteAccumulating, 2)]
    [Arguments("TRYAGAIN", RespireCommandRetryCategory.WriteAccumulating, 2)]
    [Arguments("LOADING", RespireCommandRetryCategory.WriteAccumulating, 2)]
    [Arguments("BUSY", RespireCommandRetryCategory.WriteAccumulating, 1)]
    public async Task ExecMemberErrorsResendCompleteUnitOnlyWithEveryMembersPermission(
        string code, RespireCommandRetryCategory maximum, int expectedExecs)
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1
            ? System.Text.Encoding.UTF8.GetBytes($"*2\r\n:1\r\n-{code} injected member error\r\n")
            : "*2\r\n:2\r\n$5\r\nvalue\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum },
        });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var counter = transaction.Increment("counter");
        var value = transaction.GetString("key");
        await transaction.CommitAsync();

        await Assert.That(counter.Result).IsEqualTo((long)expectedExecs);
        if (expectedExecs == 2) await Assert.That(value.Result).IsEqualTo("value");
        else await Assert.That(() => value.Result).Throws<RespireServerException>();
        await Assert.That(execs).IsEqualTo(expectedExecs);
        await Assert.That(server.ReceivedCommands.Count(command => command == "MULTI")).IsEqualTo(expectedExecs);
        await Assert.That(server.ReceivedCommands.Count(command => command == "INCR counter")).IsEqualTo(expectedExecs);
    }

    [Test]
    public async Task ExhaustedExecMemberErrorsPublishFinalResultsAndExhaustedTelemetry()
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.command.retries")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "respire.command.retry.outcome") events.AddOrUpdate((string)tag.Value!, value, (_, old) => old + value);
        });
        listener.Start();
        var execs = 0;
        await using var server = Server(exec: _ => System.Text.Encoding.UTF8.GetBytes(
            $"*2\r\n:{Interlocked.Increment(ref execs)}\r\n-TRYAGAIN injected member error\r\n"));
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero,
                MaxCommandRetryCategory = RespireCommandRetryCategory.WriteAccumulating },
        });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var counter = transaction.Increment("counter");
        var value = transaction.GetString("key");
        await transaction.CommitAsync();

        await Assert.That(counter.Result).IsEqualTo(2L);
        await Assert.That(() => value.Result).Throws<RespireServerException>();
        await Assert.That(execs).IsEqualTo(2);
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1L);
        await Assert.That(events.GetValueOrDefault("exhausted")).IsEqualTo(1L);
        await Assert.That(events.ContainsKey("succeeded")).IsFalse();
    }

    [Test]
    public async Task UnselectedQueueErrorIsNotResent()
    {
        await using var server = Server(
            exec: _ => "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray(),
            queued: command => command == "GET key" ? "-BUSY script running\r\n"u8.ToArray() : null);
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var value = transaction.GetString("key");

        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireServerException>();
        await Assert.That(() => value.Result).Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count(command => command == "EXEC")).IsEqualTo(1);
    }

    [Test]
    public async Task UncategorizedRawMemberBlocksUnitRetry()
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*2\r\n$5\r\nvalue\r\n$1\r\nx\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.ServerAdmin } });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        _ = transaction.GetString("key");
        _ = transaction.Execute(RespireCommand.Create("GET"), "other");

        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
        await Assert.That(execs).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, RespireCommandRetryCategory.ReadOnly)]
    [Arguments(true, RespireCommandRetryCategory.ReadOnly)]
    [Arguments(false, RespireCommandRetryCategory.Never)]
    [Arguments(true, RespireCommandRetryCategory.Never)]
    public async Task ScriptAndFunctionMembersUseDeclaredRetryCategory(bool functionCall, RespireCommandRetryCategory category)
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*1\r\n$1\r\nx\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var value = functionCall
            ? transaction.Functions.Execute(RespireFunction.Create("read", readOnly: true).WithRetryCategory(category), ["key"])
            : transaction.Scripts.Evaluate(RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true).WithRetryCategory(category), ["key"]);

        if (category == RespireCommandRetryCategory.Never)
        {
            await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
            await Assert.That(() => value.Result).Throws<RespireConnectionException>();
            await Assert.That(execs).IsEqualTo(1);
        }
        else
        {
            await transaction.CommitAsync();
            using var result = value.Result;
            await Assert.That(result.AsString()).IsEqualTo("x");
            await Assert.That(execs).IsEqualTo(2);
        }
    }

    [Test]
    public async Task DeclaredRawMemberCategoryPermitsUnitRetry()
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*1\r\n$1\r\nx\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var raw = transaction.Execute(RespireCommand.Create("GET").WithRetryCategory(RespireCommandRetryCategory.ReadOnly), "other");
        await transaction.CommitAsync();

        using var result = raw.Result;
        await Assert.That(result.AsString()).IsEqualTo("x");
        await Assert.That(execs).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisabledPolicyPreservesOneSend(bool policyPresent)
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = policyPresent ? new() { MaxAttempts = 1 } : null });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        _ = transaction.GetString("key");

        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
        await Assert.That(execs).IsEqualTo(1);
    }

    [Test]
    public async Task WatchedTransactionIsNotResentAfterTransientFailure()
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        await using var transaction = await client.CreateTransactionAsync(["key"]);
        _ = transaction.GetString("key");

        // WATCH state belongs to the lost connection. Only RunTransactionAsync can rebuild the body.
        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
        await Assert.That(execs).IsEqualTo(1);
    }

    [Test]
    public async Task ExhaustedAttemptsReportFinalFailureToEveryMember()
    {
        var execs = 0;
        await using var server = Server(exec: _ => { Interlocked.Increment(ref execs); return null; });
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero } });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var first = transaction.GetString("key");
        var second = transaction.Exists("key");

        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
        await Assert.That(() => first.Result).Throws<RespireConnectionException>();
        await Assert.That(() => second.Result).Throws<RespireConnectionException>();
        await Assert.That(execs).IsEqualTo(3);
    }

    [Test]
    public async Task BackoffBeyondDeadlineDoesNotResend()
    {
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CommandTimeout = TimeSpan.FromMilliseconds(500),
            RetryPolicy = new() { InitialBackoff = TimeSpan.FromSeconds(5), MaxBackoff = TimeSpan.FromSeconds(5), Jitter = 0 },
        });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        _ = transaction.GetString("key");

        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
        await Assert.That(execs).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationDuringBackoffKeepsCallerTokenAndStopsResend()
    {
        using var cancellation = new CancellationTokenSource();
        var execs = 0;
        await using var server = Server(exec: _ =>
        {
            if (Interlocked.Increment(ref execs) == 1) cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            return null;
        });
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CommandTimeout = TimeSpan.FromSeconds(30),
            RetryPolicy = new() { InitialBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(10), Jitter = 0 },
        });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var value = transaction.GetString("key");

        var error = await Failure(async () => await transaction.CommitAsync(cancellation.Token));
        await Assert.That(error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(() => value.Result).Throws<OperationCanceledException>();
        await Assert.That(execs).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetryTelemetryReportsOnlyAcceptedResends(bool circuitRejectsReplacement)
    {
        var events = new ConcurrentDictionary<string, long>();
        var categories = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.command.retries")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "respire.command.retry.outcome") events.AddOrUpdate((string)tag.Value!, value, (_, old) => old + value);
                if (tag.Key == "respire.command.retry.category") categories.Add((string)tag.Value!);
            }
        });
        listener.Start();
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*2\r\n$5\r\nvalue\r\n:1\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates MULTI/EXEC retry policy and connection history on this scripted server.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CircuitBreaker = circuitRejectsReplacement ? new() { MinimumFailureCount = 1, OpenDuration = TimeSpan.FromMinutes(1) } : null,
        });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        _ = transaction.GetString("key");
        _ = transaction.Exists("key");
        if (circuitRejectsReplacement)
        {
            await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireCircuitOpenException>();
            await Assert.That(events).IsEmpty();
            await Assert.That(execs).IsEqualTo(1);
            return;
        }
        await transaction.CommitAsync();

        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1L);
        await Assert.That(events.GetValueOrDefault("succeeded")).IsEqualTo(1L);
        await Assert.That(events.ContainsKey("exhausted")).IsFalse();
        await Assert.That(categories.Distinct().ToArray()).IsEquivalentTo([nameof(RespireCommandRetryCategory.ReadOnly)]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetryAttemptMetricPublishesOutsideTransportWriteGate(bool throwFromListener)
    {
        RespireClient? client = null;
        var published = 0;
        var underGate = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.command.retries")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "respire.command.retry.outcome" || (string?)tag.Value != "attempted") continue;
                Interlocked.Increment(ref published);
                // A listener that blocks or re-enters the client must not run inside transport admission.
                if (Volatile.Read(ref client) is { } current && AnyWriteGateHeld(current)) Interlocked.Increment(ref underGate);
                if (throwFromListener) throw new InvalidOperationException("Injected retry listener failure.");
            }
        });
        listener.Start();
        var execs = 0;
        await using var server = Server(exec: _ => Interlocked.Increment(ref execs) == 1 ? null : "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client gives the telemetry listener a dedicated transport gate to inspect during retry publication.
        await using var connected = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        Volatile.Write(ref client, connected);
        await using var transaction = connected.CreateTransaction();
        var value = transaction.GetString("key");
        await transaction.CommitAsync();

        await Assert.That(value.Result).IsEqualTo("value");
        await Assert.That(execs).IsEqualTo(2);
        await Assert.That(published).IsEqualTo(1);
        await Assert.That(underGate).IsEqualTo(0);
    }

    private static bool AnyWriteGateHeld(RespireClient client)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var connections = (RespireConnection?[])typeof(RespireConnectionMultiplexer).GetField("_connections", flags)!
            .GetValue(client.Core.Multiplexer)!;
        foreach (var connection in connections)
        {
            if (connection is null) continue;
            var gate = typeof(RespireConnection).GetField("_writeGate", flags)!.GetValue(connection)!;
            if ((bool)gate.GetType().GetProperty("IsHeldByCurrentThread")!.GetValue(gate)!) return true;
        }
        return false;
    }

    // The fake server keeps MULTI state per connection. A null EXEC reply closes every connection
    // after MULTI and the queued commands were received, so the unit's effect may have reached Redis.
    private static FakeRespServer Server(Func<string, byte[]?> exec, Func<string, byte[]?>? queued = null)
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        var transactions = new ConcurrentDictionary<int, bool>();
        server.ReplyOverride = (connection, command) =>
        {
            if (command == "MULTI") { transactions[connection] = true; return FakeRespServer.OkReply; }
            if (command == "EXEC")
            {
                transactions[connection] = false;
                var reply = exec(command);
                if (reply is null) server.CloseConnections();
                return reply;
            }
            if (transactions.GetValueOrDefault(connection))
                return queued?.Invoke(command) ?? "+QUEUED\r\n"u8.ToArray();
            return Setup(command);
        };
        return server;
    }

    private static byte[]? Setup(string command) => command switch
    {
        _ when command.StartsWith("HELLO", StringComparison.Ordinal) => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
        "CLIENT ID" => ":1\r\n"u8.ToArray(),
        _ => null,
    };

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = RespireProtocol.Resp2,
        ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(3),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };

    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }
}
