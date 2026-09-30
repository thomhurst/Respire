using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeServerFaultTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    public async Task FaultValidationRejectsInvalidRulesWithoutInstallingThem()
    {
        await using var server = new RespireFakeServer();
        await Assert.That(() => server.InjectFault("SET key", RespireFakeFault.Loading())).Throws<ArgumentException>();
        await Assert.That(() => server.InjectFault("SET", RespireFakeFault.Loading(), 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireFakeFault.Delay(TimeSpan.FromMilliseconds(-1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireFakeFault.Moved(16384, new("target"))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireFakeFault.Moved(42, new("target\r\n+OK"))).Throws<ArgumentException>();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await Assert.That(await client.SetAsync("key", "value")).IsTrue();
        await server.DisposeAsync();
        await Assert.That(() => server.InjectFault("SET", RespireFakeFault.Loading())).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RejectionConsumesExactlyOneBatchReply(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1, Protocol = (RespProtocol)protocol });
        using var fault = server.InjectFault("SET", RespireFakeFault.Loading());
        using var batch = client.CreateBatch();
        var rejected = batch.Strings.Set("rejected", "bad");
        var accepted = batch.Strings.Set("accepted", "ok");
        var read = batch.Strings.GetString("accepted");
        await Assert.That(async () => await batch.ExecuteAsync()).Throws<RespireServerException>();
        await Assert.That(async () => await rejected).Throws<RespireServerException>();
        await Assert.That(await accepted).IsTrue();
        await Assert.That(await read).IsEqualTo("ok");
        await Assert.That(await client.GetStringAsync("rejected")).IsNull();
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RulesMatchOwnedBinaryArgumentsInRegistrationOrderAndReset(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        byte[] key = [0, 255, 65];
        using var first = server.InjectFault("sEt", RespireFakeFault.Loading(), firstArgument: key);
        using var repeated = server.InjectFault("SET", RespireFakeFault.ReadOnly(), occurrences: null, firstArgument: key);
        key[2] = 66; // The registration must own its argument bytes.
        await client.SetAsync(key, "unmatched");
        var loading = await Assert.That(async () => await client.SetAsync(new byte[] { 0, 255, 65 }, "bad"))
            .Throws<RespireServerException>();
        await Assert.That(loading!.Message).StartsWith("LOADING");
        for (var index = 0; index < 2; index++)
        {
            var readOnly = await Assert.That(async () => await client.SetAsync(new byte[] { 0, 255, 65 }, "bad"))
                .Throws<RespireServerException>();
            await Assert.That(readOnly!.Message).StartsWith("READONLY");
        }
        await Assert.That(first.MatchedCount).IsEqualTo(1);
        await Assert.That(repeated.MatchedCount).IsEqualTo(2);
        await Assert.That(first.ExecutionCount + repeated.ExecutionCount).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync(new byte[] { 0, 255, 65 })).IsNull();
        server.ResetFaults();
        await client.SetAsync(new byte[] { 0, 255, 65 }, "after-reset");
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("unmatched");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 3)]
    public async Task GateDefinesExecutionBoundaryAndCancellationDoesNotUndoAcceptedCommands(bool afterExecution, int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Connections = 1, Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        await using var observer = await RespireClient.ConnectAsync(options);
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("INCR", RespireFakeFault.Pause(gate, afterExecution));
        using var cancel = new CancellationTokenSource();
        var command = client.ExecuteAsync("INCR", ["counter"], cancellationToken: cancel.Token).AsTask();
        await fault.Matched.WaitAsync(Limit);
        await Assert.That(command.IsCompleted).IsFalse();
        await Assert.That(await observer.GetStringAsync("counter")).IsEqualTo(afterExecution ? "1" : null);
        cancel.Cancel();
        await Assert.That(async () => { using var reply = await command.WaitAsync(Limit); }).Throws<OperationCanceledException>();
        gate.Release();
        // This connection's next reply proves the cancelled response drained in FIFO order.
        await Assert.That(await client.GetStringAsync("counter").AsTask().WaitAsync(Limit)).IsEqualTo("1");
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(fault.ExecutionCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 3)]
    public async Task DisconnectNeverReplaysAnAmbiguouslyAcceptedMutation(bool afterExecution, int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with
        {
            Connections = 1, Protocol = (RespProtocol)protocol,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 },
        };
        await using var client = await RespireClient.ConnectAsync(options);
        await using var observer = await RespireClient.ConnectAsync(options);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.State == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        using var fault = server.InjectFault("INCR", RespireFakeFault.Disconnect(afterExecution), occurrences: null);
        await Assert.That(async () => { using var reply = await client.ExecuteAsync("INCR", "counter").AsTask().WaitAsync(Limit); })
            .Throws<RespireConnectionException>();
        await Assert.That(await observer.GetStringAsync("counter")).IsEqualTo(afterExecution ? "1" : null);
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(fault.ExecutionCount).IsEqualTo(afterExecution ? 1 : 0);
        fault.Dispose();
        // Normal standalone sends fail fast while scheduling replacement. Trigger that
        // recovery with a safe read, then await its lifecycle event without polling.
        try { await client.GetStringAsync("counter"); }
        catch (RespireConnectionException) { }
        await recovered.Task.WaitAsync(Limit);
        // Reconnect policy restores transport for future work, without resending INCR.
        await Assert.That(await client.GetStringAsync("counter").AsTask().WaitAsync(Limit)).IsEqualTo(afterExecution ? "1" : null);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemovingRuleReleasesPauseOrLatencyWithoutClearingData(bool latency)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
        using var fault = server.InjectFault("SET", latency ? RespireFakeFault.Delay(TimeSpan.FromDays(1)) : RespireFakeFault.Pause(new()));
        var command = client.SetAsync("key", "value").AsTask();
        await fault.Matched.WaitAsync(Limit);
        await Assert.That(command.IsCompleted).IsFalse();
        if (latency) server.ResetFaults();
        else fault.Dispose();
        await Assert.That(await command.WaitAsync(Limit)).IsTrue();
        await Assert.That(fault.ExecutionCount).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DisposalAbortsHeldCommandsAndJoinsServerLoops(bool disposeServer, bool afterExecution)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
        using var fault = server.InjectFault("SET", RespireFakeFault.Pause(new(), afterExecution));
        var command = client.SetAsync("key", "value").AsTask();
        await fault.Matched.WaitAsync(Limit);
        await (disposeServer ? server.DisposeAsync().AsTask() : client.DisposeAsync().AsTask()).WaitAsync(Limit);
        await Assert.That(async () => await command.WaitAsync(Limit)).Throws<Exception>();
        await server.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(fault.ExecutionCount).IsEqualTo(afterExecution ? 1 : 0);
    }

    [Test]
    public async Task OneShotReservationIsAtomicAcrossConnections()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 4 });
        using var fault = server.InjectFault("INCR", RespireFakeFault.Loading());
        var calls = Enumerable.Range(0, 32).Select(async _ =>
        {
            try { using var reply = await client.ExecuteAsync("INCR", "counter"); return true; }
            catch (RespireServerException error) when (error.Message.StartsWith("LOADING", StringComparison.Ordinal)) { return false; }
        });
        await Assert.That((await Task.WhenAll(calls).WaitAsync(Limit)).Count(success => success)).IsEqualTo(31);
        await Assert.That(await client.GetStringAsync("counter")).IsEqualTo("31");
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(fault.ExecutionCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MovedIsAnExplicitRejectionWithoutFakeClusterRouting(int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Connections = 1, Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        var destination = new RespireEndpoint("other-fake.invalid", 6380);
        using var fault = server.InjectFault("SET", RespireFakeFault.Moved(42, destination));
        var error = await Assert.That(async () => await client.SetAsync("key", "not-applied")).Throws<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo("MOVED 42 other-fake.invalid:6380");
        await Assert.That(await client.GetStringAsync("key")).IsNull();
        await Assert.That(fault.ExecutionCount).IsEqualTo(0);
        // The transport rejects a redirect endpoint instead of opening a real socket.
        await Assert.That(async () => await RespireClient.ConnectAsync(options with { Endpoints = [destination] }))
            .Throws<NotSupportedException>();
        await Assert.That(await client.SetAsync("key", "after-error")).IsTrue();
    }

    [Test]
    public async Task FragmentedCommandMatchesOnceAndUnusedRulesCancelTheirObservation()
    {
        await using var server = new RespireFakeServer();
        using var fault = server.InjectFault("SET", RespireFakeFault.Loading());
        using var unused = server.InjectFault("GET", RespireFakeFault.Loading());
        var options = server.CreateOptions();
        await using var stream = await options.TestingStreamFactory!(options.Endpoints[0].Host, 6379, default);
        foreach (var value in "*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray())
            await stream.WriteAsync(new byte[] { value });
        await fault.Matched.WaitAsync(Limit);
        var expected = "-LOADING Redis is loading the dataset in memory\r\n"u8.ToArray();
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual).AsTask().WaitAsync(Limit);
        await Assert.That(Encoding.UTF8.GetString(actual)).IsEqualTo(Encoding.UTF8.GetString(expected));
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        unused.Dispose();
        await Assert.That(async () => await unused.Matched.WaitAsync(Limit)).Throws<OperationCanceledException>();
    }
}
