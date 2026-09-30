using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class NativeLockCommandTests
{
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
    [Arguments(true, "WRONGTYPE key has wrong type")]
    [Arguments(true, "ERR arbitrary server failure")]
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
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) }, Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(100),
        });
        await Assert.That(async () => await ExecuteAsync(client, extend)).Throws<RespireTimeoutException>();
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
