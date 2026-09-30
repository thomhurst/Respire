using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class FencedLockWireTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcceptedAcquisitionIsNotReplayedAfterCancellationOrDisconnect(bool disconnect)
    {
        await using var server = new FakeRespServer(3, "$1\r\n1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal),
            CloseConnectionAfterCommand = disconnect ? 1 : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            CommandTimeout = null,
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = new RespireCoordination(client).TryAcquireFencedLockAsync("{job}:lease", "{job}:counter",
            TimeSpan.FromSeconds(30), cancellation.Token).AsTask();
        while (server.CommandsSeen == 0) await Task.Delay(5, cancellation.Token);
        if (!disconnect) cancellation.Cancel();
        if (disconnect) await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        else await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVAL", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task SameKeyAndInvalidDurationFailBeforeConnecting()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("unused.invalid")] });
        var coordination = new RespireCoordination(client);
        await Assert.That(async () => await coordination.TryAcquireFencedLockAsync("same", "same", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await coordination.TryAcquireFencedLockAsync("lock", "counter", TimeSpan.FromTicks(9999)))
            .Throws<ArgumentOutOfRangeException>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => await coordination.TryAcquireFencedLockAsync("lock", "counter", TimeSpan.FromSeconds(1), canceled.Token))
            .Throws<OperationCanceledException>();
        await using var empty = default(RespireFencedLockAttempt);
        await Assert.That(empty.Acquired).IsFalse();
        await Assert.That(() => empty.Lock).Throws<RespireLockNotAcquiredException>();
    }

    [Test]
    public async Task NoScriptFallbackOccursOnlyAfterDefinitiveRejection()
    {
        await using var server = new FakeRespServer("-NOSCRIPT No matching script\r\n"u8.ToArray(), "$1\r\n7\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var attempt = await new RespireCoordination(client).TryAcquireFencedLockAsync("{job}:lease", "{job}:counter", TimeSpan.FromMinutes(1));
        await Assert.That(attempt.Lock.FencingToken).IsEqualTo(7);
        await Assert.That(server.ReceivedCommands[0].StartsWith("EVALSHA ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(server.ReceivedCommands[1].StartsWith("EVAL ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }
}
