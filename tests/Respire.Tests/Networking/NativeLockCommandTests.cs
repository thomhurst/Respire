using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class NativeLockCommandTests
{
    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task MissingLuaEnginePreservesManagedOwnershipWithoutFencing(int protocol, bool extend)
    {
        var missingEngine = true;
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLIENT ID" => ":41\r\n"u8.ToArray(),
                "INFO scriptingengines" => Encoding.UTF8.GetBytes("$" + Encoding.UTF8.GetByteCount(AbsentEngines)
                    + "\r\n" + AbsentEngines + "\r\n"),
                var text when text.StartsWith("DELEX ", StringComparison.Ordinal) => UnknownDelex,
                var text when text.StartsWith("DELIFEQ ", StringComparison.Ordinal) => UnknownDelifeq,
                var text when text.StartsWith("SET ", StringComparison.Ordinal) && text.Contains(" IFEQ ", StringComparison.Ordinal)
                    => UnsupportedSet,
                var text when text.StartsWith("EVALSHA ", StringComparison.Ordinal) => "-NOSCRIPT missing\r\n"u8.ToArray(),
                var text when text.StartsWith("EVAL ", StringComparison.Ordinal) => missingEngine
                    ? "-ERR Could not find scripting engine 'lua'\r\n"u8.ToArray() : One,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var error = await Assert.That(async () =>
            {
                if (extend) await mutex.ResetExpiryAsync(TimeSpan.FromMinutes(1), deadline.Token);
                else await mutex.ReleaseAsync(deadline.Token);
            }).Throws<RespireScriptingEngineUnavailableException>();
            await Assert.That(error!.ServerError.Code).IsEqualTo("ERR");
            await Assert.That(error.ServerError.CommandName).IsEqualTo("EVAL");
            await Assert.That(error.Endpoint.Port).IsEqualTo(server.Port);
            await Assert.That(mutex.IsReleased).IsFalse();
            // The capability probe includes SKIPME; a fence of this owner does not.
            await Assert.That(server.ReceivedCommands).DoesNotContain("CLIENT KILL ID 41");
            missingEngine = false;
            if (extend) await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromMinutes(1), deadline.Token)).IsTrue();
            else await Assert.That(await mutex.ReleaseAsync(deadline.Token)).IsEqualTo(LockReleaseOutcome.Released);
        }
        finally
        {
            missingEngine = false;
            await mutex.DisposeAsync();
        }
    }

    private const string AbsentEngines = "# Scripting Engines\r\nengines_count:0\r\nengines_total_used_memory:0\r\n";

    [Test]
    [Arguments(0L)]
    [Arguments(-1L)]
    [Arguments(long.MinValue)]
    public async Task InvalidRenewalDurationFailsBeforeCapabilityProbing(long ticks)
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        await Assert.That(async () => await client.Locks.ResetExpiryAsync("key", "owner", TimeSpan.FromTicks(ticks)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(1L, 1L)]
    [Arguments(9999L, 1L)]
    [Arguments(10000L, 1L)]
    [Arguments(15000L, 2L)]
    [Arguments(long.MaxValue, long.MaxValue / TimeSpan.TicksPerMillisecond)]
    public async Task AcquisitionAndRawRenewalUseTheSameWholeMilliseconds(long ticks, long milliseconds)
    {
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var expiry = TimeSpan.FromTicks(ticks);
        var attempt = await client.Locks.AcquireAsync("key", expiry);
        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(attempt.Lock.Duration.Ticks).IsEqualTo(milliseconds * TimeSpan.TicksPerMillisecond);
        await Assert.That(await client.Locks.TryTakeAsync("raw", "owner", expiry)).IsTrue();
        await Assert.That(await client.Locks.ResetExpiryAsync("raw", "owner", expiry)).IsTrue();
        await Assert.That(server.ReceivedCommands[0]).EndsWith($" NX PX {milliseconds}");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo($"SET raw owner NX PX {milliseconds}");
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo($"SET raw owner IFEQ owner PX {milliseconds}");
    }

    internal static readonly byte[] UnknownDelex = "-ERR unknown command 'DELEX', with args beginning with: \r\n"u8.ToArray();
    internal static readonly byte[] UnknownDelifeq = "-ERR unknown command 'DELIFEQ', with args beginning with: \r\n"u8.ToArray();
    private static readonly byte[] UnsupportedSet = "-ERR syntax error\r\n"u8.ToArray();
    private static readonly byte[] One = ":1\r\n"u8.ToArray();

    [Test]
    public async Task UnsupportedCommandsAreRememberedPerConnection()
    {
        await using var server = new FakeRespServer(UnsupportedSet, One, UnknownDelex, UnknownDelifeq, One, One, One);
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");
        for (var i = 0; i < 2; i++)
        {
            await Assert.That(await client.Locks.ResetExpiryAsync("lock", "owner", TimeSpan.FromSeconds(5))).IsTrue();
            await Assert.That(await client.Locks.ReleaseAsync("lock", "owner")).IsTrue();
        }
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SET tenant:lock owner IFEQ owner PX 5000",
            $"EVALSHA {LockCommands.ExtendScript.Sha1} 1 tenant:lock owner 5000",
            "DELEX tenant:lock IFEQ owner", "DELIFEQ tenant:lock owner",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 tenant:lock owner",
            $"EVALSHA {LockCommands.ExtendScript.Sha1} 1 tenant:lock owner 5000",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 tenant:lock owner",
        });
    }

    [Test]
    public async Task ValkeyDeletionSkipsUnsupportedRedisCommandAfterFirstAttempt()
    {
        await using var server = new FakeRespServer(UnknownDelex, One, ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(await client.Locks.ReleaseAsync("lock", "owner")).IsTrue();
        await Assert.That(await client.Locks.ReleaseAsync("lock", "wrong")).IsFalse();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "DELEX lock IFEQ owner", "DELIFEQ lock owner", "DELIFEQ lock wrong",
        });
    }

    [Test]
    [Arguments(false, "NOPERM command denied")]
    [Arguments(true, "NOPERM command denied")]
    [Arguments(false, "ERR syntax error")]
    [Arguments(false, "ERR unknown command 'another', with args beginning with:")]
    [Arguments(false, "ERR unknown command 'DELEX'")]
    [Arguments(true, "WRONGTYPE key has wrong type")]
    [Arguments(true, "ERR arbitrary server failure")]
    [Arguments(true, "ERR proxy reported syntax error after forwarding")]
    public async Task OtherServerErrorsNeverTriggerFallback(bool extend, string error)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes($"-{error}\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await ExecuteAsync(client, extend)).Throws<RespireServerException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task UncertainOutcomeNeverTriggersFallback(bool extend, bool disconnect)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(One)
        {
            CloseConnectionAfterCommand = disconnect ? 1 : null,
            SuppressReply = _ => { arrived.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var pending = ExecuteAsync(client, extend, cancellation.Token).AsTask();
        if (!disconnect)
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        }
        else await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReconnectionDoesNotInheritUnsupportedCapabilities(bool extend)
    {
        byte[][] replies = extend ? [UnsupportedSet, One] : [UnknownDelex, UnknownDelifeq, One];
        await using var server = new FakeRespServer(2, replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(await ExecuteAsync(client, extend)).IsTrue();
        var original = await client.AcquireConnectionAsync(CancellationToken.None);
        await original.DisposeAsync();
        // Change the scripted first reply before opening a replacement socket.
        replies[0] = extend ? FakeRespServer.OkReply : One;
        using var reconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.Core.Multiplexer.GetHealthyConnectionAsync(reconnectTimeout.Token);
        await Assert.That(await ExecuteAsync(client, extend)).IsTrue();
        var commands = server.ReceivedCommands;
        await Assert.That(commands[^1]).StartsWith(extend ? "SET " : "DELEX ");
        await Assert.That(server.ReceivedConnectionIds[^1]).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterRedirectRechecksCapabilitiesAndAskingPrecedesEveryFallback(bool ask)
    {
        await using var target = new FakeRespServer(ask
            ? [FakeRespServer.OkReply, UnknownDelex, FakeRespServer.OkReply, UnknownDelifeq,
                FakeRespServer.OkReply, "-NOSCRIPT missing\r\n"u8.ToArray(), FakeRespServer.OkReply, One]
            : [One]);
        var slot = ClusterHash.GetSlot("resource");
        await using var source = new FakeRespServer(UnknownDelex, UnknownDelifeq,
            Encoding.ASCII.GetBytes($"-{(ask ? "ASK" : "MOVED")} {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var seed = new FakeRespServer(Topology(source.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsTrue();
        await Assert.That(source.ReceivedCommands).Count().IsEqualTo(3);
        await Assert.That(target.ReceivedCommands[ask ? 1 : 0]).IsEqualTo("DELEX resource IFEQ owner");
        if (ask)
        {
            await Assert.That(target.ReceivedCommands.Where((_, index) => index % 2 == 0))
                .IsEquivalentTo(new[] { "ASKING", "ASKING", "ASKING", "ASKING" });
            await Assert.That(target.ReceivedCommands[^1]).StartsWith("EVAL ");
        }
        else await Assert.That(target.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TimeoutNeverTriggersFallback(bool extend)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { written.TrySetResult(); return true; },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) }, Connections = 1,
            CommandTimeout = null,
        });
        var connection = client.Core.Multiplexer.GetConnection();
        var execution = ExecuteAsync(client, extend).AsTask();
        await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Admission has finished and replies are suppressed. Complete the live source through
        // the real timeout transition; a setup/JIT delay must not expire before any command is sent.
        await Assert.That(connection.InspectForTests().Inflight.TryPeek(out var source)).IsTrue();
        RespireTimeoutDiagnostics? diagnostics = null;
        await Assert.That(source!.TrySetTimedOut(source.State, TimeSpan.FromMilliseconds(100), ref diagnostics, connection)).IsTrue();
        await Assert.That(async () => await execution.WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireTimeoutException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ManagedRedirectFencesTheNativeRenewalDestination(bool ask)
    {
        var renewalArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(3, ":52\r\n"u8.ToArray(), One, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("SET ", StringComparison.Ordinal)) return false;
                renewalArrived.TrySetResult();
                return true;
            },
        };
        var slot = ClusterHash.GetSlot("resource");
        await using var source = new FakeRespServer(FakeRespServer.OkReply, ":41\r\n"u8.ToArray(), One,
            Encoding.ASCII.GetBytes($"-{(ask ? "ASK" : "MOVED")} {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var seed = new FakeRespServer(Topology(source.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        var renewal = mutex.ResetExpiryAsync(TimeSpan.FromMinutes(1), cancellation.Token).AsTask();
        await renewalArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await renewal).Throws<OperationCanceledException>();
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(target.ReceivedCommands).Contains("CLIENT KILL ID 52");
        await Assert.That(source.ReceivedCommands).DoesNotContain("CLIENT KILL ID 41");
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("EVAL", StringComparison.Ordinal))).IsFalse();
        await mutex.DisposeAsync();
    }
    private static ValueTask<bool> ExecuteAsync(RespireClient client, bool extend, CancellationToken cancellationToken = default)
        => extend ? client.Locks.ResetExpiryAsync("resource", "owner", TimeSpan.FromSeconds(5), cancellationToken)
            : client.Locks.ReleaseAsync("resource", "owner", cancellationToken);

    private static byte[] Topology(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
}
