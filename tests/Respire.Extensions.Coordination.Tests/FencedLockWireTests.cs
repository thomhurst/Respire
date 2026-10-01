using System.Diagnostics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class FencedLockWireTests
{
    [Test]
    [NotInParallel]
    public async Task CancellationAfterSuccessfulReadWriteReplyReleasesUnreturnedLease()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.operation.name") is "EVALSHA"
                    && activity.GetTagItem("server.port") is int port && port == server.Port)
                    cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);

        await Assert.That(async () => await new RespireCoordination(client)
            .TryAcquireReadLockAsync("{job}:rw", TimeSpan.FromSeconds(30), cancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.All(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task CancelledReadWriteVerifyAndReleaseKeepLeaseReleasable()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var attempt = await new RespireCoordination(client).TryAcquireReadLockAsync("{job}:rw", TimeSpan.FromSeconds(30))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(attempt.Acquired).IsTrue();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(async () => await attempt.Lock.VerifyStillHeldAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await attempt.Lock.ReleaseAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(attempt.Lock.IsReleased).IsFalse();

        await Assert.That(await attempt.Lock.ReleaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
    }

    [Test]
    public async Task FailedReadWriteReleaseCanBeRetried()
    {
        var evalCount = 0;
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                && Interlocked.Increment(ref evalCount) == 2,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var attempt = await new RespireCoordination(client)
            .TryAcquireWriteLockAsync("{job}:rw", TimeSpan.FromSeconds(30))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(attempt.Acquired).IsTrue();

        using var cancellation = new CancellationTokenSource();
        var pending = attempt.Lock.ReleaseAsync(cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < 2) await Task.Delay(10, timeout.Token);
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(attempt.Lock.IsReleased).IsFalse();
        await server.SendRawAsync(":1\r\n"u8.ToArray());
        await Assert.That(await attempt.Lock.ReleaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(3);
    }

    [Test]
    public async Task MaximumReadWriteLeaseDurationDoesNotOverflowLocalEstimate()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var attempt = await new RespireCoordination(client).TryAcquireWriteLockAsync("{job}:rw", TimeSpan.MaxValue)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(attempt.Lock.RemainingEstimate > TimeSpan.FromDays(365 * 1000)).IsTrue();
        await Assert.That(await attempt.Lock.ResetExpiryAsync(TimeSpan.MaxValue)).IsTrue();
        await Assert.That(attempt.Lock.IsReleased).IsFalse();
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsTrue();
    }

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
    [NotInParallel] // Activity completion deterministically cancels after the script reply was parsed.
    public async Task CancellationAfterSuccessfulReplyReleasesTheUnreturnedLease()
    {
        await using var server = new FakeRespServer("$1\r\n1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var repliesCompleted = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.operation.name") is not "EVALSHA" || activity.GetTagItem("server.port") is not int port
                    || port != server.Port) return;
                Interlocked.Increment(ref repliesCompleted);
                cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);
        var error = await Assert.That(async () => await new RespireCoordination(client)
            .TryAcquireFencedLockAsync("{job}:lease", "{job}:counter", TimeSpan.FromSeconds(30), cancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(repliesCompleted).IsEqualTo(1);
        var commands = server.ReceivedCommands;
        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(commands[0].StartsWith("EVALSHA ", StringComparison.Ordinal)).IsTrue();
        var owner = System.Text.Encoding.ASCII.GetString(server.ReceivedArguments[0][5]);
        await Assert.That(commands[1]).IsEqualTo($"DELEX {{job}}:lease IFEQ {owner}");
    }

    [Test]
    [Arguments("invalid")]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("9223372036854775808")]
    public async Task MalformedAcquisitionReplyReleasesTheUnreturnedLease(string token)
    {
        var reply = System.Text.Encoding.ASCII.GetBytes($"${token.Length}\r\n{token}\r\n");
        await using var server = new FakeRespServer(reply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await new RespireCoordination(client)
            .TryAcquireFencedLockAsync("{job}:lease", "{job}:counter", TimeSpan.FromSeconds(30))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ThrowsExactly<RespireProtocolException>();
        var commands = server.ReceivedCommands;
        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(commands[0].StartsWith("EVALSHA ", StringComparison.Ordinal)).IsTrue();
        var owner = System.Text.Encoding.ASCII.GetString(server.ReceivedArguments[0][5]);
        await Assert.That(commands[1]).IsEqualTo($"DELEX {{job}}:lease IFEQ {owner}");
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
    public async Task AcquireWaitsForTrackedInvalidationAndRetriesAtomicScript()
    {
        var attempts = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET tenant:{job}:lease" => "$-1\r\n"u8.ToArray(),
                var value when value.StartsWith("PTTL ", StringComparison.Ordinal)
                    => ":-1\r\n"u8.ToArray(),
                var value when value.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    => Interlocked.Increment(ref attempts) == 1 ? "_\r\n"u8.ToArray() : "$1\r\n1\r\n"u8.ToArray(),
                var value when value.StartsWith("DELEX tenant:{job}:lease IFEQ ", StringComparison.Ordinal)
                    => ":1\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            ClientSideCache = new(),
        });
        var view = client.WithKeyPrefix("tenant:");
        var pending = new RespireCoordination(view).AcquireFencedLockAsync(
            "{job}:lease", "{job}:counter", TimeSpan.FromSeconds(10)).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref attempts) == 0) await Task.Delay(5, timeout.Token);

        var commandConnection = server.ReceivedConnectionIds[
            server.ReceivedCommands.ToList().IndexOf("GET tenant:{job}:lease")];
        await server.SendRawAsync(
            ">2\r\n+invalidate\r\n*1\r\n$18\r\ntenant:{job}:lease\r\n"u8.ToArray(), commandConnection);

        await using var lease = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(lease.FencingToken).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    [Test]
    public async Task AcquireWaitClampsExpiryBeyondSemaphoreTimeoutLimit()
    {
        var attempts = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET lease" => "$-1\r\n"u8.ToArray(),
                // 30 days: beyond SemaphoreSlim's Int32.MaxValue millisecond timeout limit.
                "PTTL lease" => ":2592000000\r\n"u8.ToArray(),
                var value when value.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    => Interlocked.Increment(ref attempts) == 1 ? "_\r\n"u8.ToArray() : "$1\r\n1\r\n"u8.ToArray(),
                var value when value.StartsWith("DELEX lease IFEQ ", StringComparison.Ordinal)
                    => ":1\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            ClientSideCache = new(),
        });
        var pending = new RespireCoordination(client).AcquireFencedLockAsync(
            "lease", "counter", TimeSpan.FromDays(30)).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Contains("PTTL lease")) await Task.Delay(5, timeout.Token);
        await Task.Delay(50, timeout.Token);
        await Assert.That(pending.IsFaulted).IsFalse();

        var commandConnection = server.ReceivedConnectionIds[
            server.ReceivedCommands.ToList().IndexOf("GET lease")];
        await server.SendRawAsync(">2\r\n+invalidate\r\n*1\r\n$5\r\nlease\r\n"u8.ToArray(), commandConnection);

        await using var lease = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(2);
    }

    [Test]
    public async Task AcquireWaitCancellationStopsWaitingWithoutRetrying()
    {
        var attempts = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET lease" => "$-1\r\n"u8.ToArray(),
                "PTTL lease" => ":-1\r\n"u8.ToArray(),
                var value when value.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    => ReturnContended(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            ClientSideCache = new(),
        });
        using var cancellation = new CancellationTokenSource();
        var pending = new RespireCoordination(client).AcquireFencedLockAsync(
            "lease", "counter", TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref attempts) == 0) await Task.Delay(5, timeout.Token);

        cancellation.Cancel();

        await Assert.That(async () => await pending).ThrowsExactly<OperationCanceledException>();
        await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(1);

        byte[] ReturnContended()
        {
            Interlocked.Increment(ref attempts);
            return "_\r\n"u8.ToArray();
        }
    }

    [Test]
    public async Task AcquireWaitRequiresClientTracking()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = ["unused.invalid"] });

        await Assert.That(async () => await new RespireCoordination(client)
                .AcquireFencedLockAsync("lease", "counter", TimeSpan.FromSeconds(10)))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task ClientDisposalStopsAcquireWait()
    {
        var attempts = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET lease" => "$-1\r\n"u8.ToArray(),
                "PTTL lease" => ":-1\r\n"u8.ToArray(),
                var value when value.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    => ReturnContended(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            ClientSideCache = new(),
        });
        var pending = new RespireCoordination(client).AcquireFencedLockAsync(
            "lease", "counter", TimeSpan.FromSeconds(10)).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref attempts) == 0) await Task.Delay(5, timeout.Token);
        await Task.Delay(50, timeout.Token);

        await client.DisposeAsync();

        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<ObjectDisposedException>();

        byte[] ReturnContended()
        {
            Interlocked.Increment(ref attempts);
            return "_\r\n"u8.ToArray();
        }
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
