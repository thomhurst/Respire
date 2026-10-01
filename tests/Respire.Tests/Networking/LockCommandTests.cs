using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class LockCommandTests
{
    [Test]
    public async Task RetiredConnectionMeansLockReleaseWasNotSubmitted()
    {
        var error = new Respire.Networking.RespireConnectionRetiredException("localhost", 6379);

        await Assert.That(LockCommands.IsUnsubmitted(error)).IsTrue();
    }

    [Test]
    public async Task ClosedConnectionRejectsEnqueueAsNotSubmitted()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await Respire.Networking.RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using (await connection.SendAsync(new Respire.Commands.RawCommand(FakeRespServer.PingFrame)))
        {
        }

        server.CloseConnections();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (connection.IsConnected) await Task.Delay(10, timeout.Token);

        // The connection is already dead when the command reaches the write gate, so nothing was
        // appended. Lock release relies on this type to keep ownership retryable.
        var error = await Assert.That(async () =>
                await connection.SendAsync(new Respire.Commands.RawCommand(FakeRespServer.PingFrame)))
            .Throws<RespireConnectionException>();
        await Assert.That(error).IsTypeOf<Respire.Networking.RespireConnectionClosedBeforeSendException>();
        await Assert.That(LockCommands.IsUnsubmitted(error!)).IsTrue();
        await Assert.That(error!.Message).IsEqualTo(connection.CloseError!.Message);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task ConnectionLossAfterWriteIsNotReportedAsUnsubmitted()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply) { SuppressReply = _ => true };
        await using var connection = await Respire.Networking.RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var pending = connection.SendAsync(new Respire.Commands.RawCommand(FakeRespServer.PingFrame)).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);

        server.CloseConnections();

        // The command reached the server, so its failure must stay uncertain.
        var error = await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        await Assert.That(LockCommands.IsUnsubmitted(error!)).IsFalse();
    }

    [Test]
    public async Task RetirementRacingWritesEitherAcceptsOrRejectsBeforeSending()
    {
        // Lock release treats RespireConnectionRetiredException as proof that nothing was sent.
        // Race retirement against many writers: every command must either be accepted and reach
        // the server, or be rejected with that exception without reaching it.
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await Respire.Networking.RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = Enumerable.Range(0, 256).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                using var reply = await connection.SendAsync(
                    new Respire.Commands.RawCommand(FakeRespServer.PingFrame), timeout.Token);
                return true;
            }
            catch (Respire.Networking.RespireConnectionRetiredException)
            {
                return false;
            }
        })).ToArray();

        start.SetResult();
        await Task.Yield();
        var retirement = connection.RetireAsync();
        var accepted = (await Task.WhenAll(sends).WaitAsync(timeout.Token)).Count(sent => sent);
        await retirement.WaitAsync(timeout.Token);

        await Assert.That(server.CommandsSeen).IsEqualTo(accepted);
    }

    [Test]
    public async Task LockCommands_WriteExpectedFramesAndParseReplies()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nowner\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.FromSeconds(30))).IsTrue();
        var queriedToken = await client.Locks.GetOwnerTokenAsync("resource");
        await Assert.That(queriedToken == (RespireLockToken)"owner").IsTrue();
        await Assert.That(await client.Locks.ResetExpiryAsync("resource", "owner", TimeSpan.FromSeconds(45))).IsTrue();
        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsTrue();

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SET resource owner NX PX 30000",
            "GET resource",
            "SET resource owner IFEQ owner PX 45000",
            "DELEX resource IFEQ owner",
        });
    }

    [Test]
    public async Task LockCommands_ReturnFalseWhenAcquireOrOwnershipCheckFails()
    {
        await using var server = new FakeRespServer(
            "$-1\r\n"u8.ToArray(),
            "$-1\r\n"u8.ToArray(),
            "$-1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.FromSeconds(30))).IsFalse();
        await Assert.That(await client.Locks.GetOwnerTokenAsync("resource")).IsNull();
        await Assert.That(await client.Locks.ResetExpiryAsync("resource", "owner", TimeSpan.FromSeconds(45))).IsFalse();
        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsFalse();
    }

    [Test]
    public async Task LockCommands_GetOwnerTokenPreservesBinaryTokenForOwnershipChecks()
    {
        byte[] expectedToken = [0xFF, 0x00, 0xC3, 0x28];
        byte[] binaryTokenReply = [.. "$4\r\n"u8, .. expectedToken, .. "\r\n"u8];
        await using var server = new FakeRespServer(binaryTokenReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var token = await client.Locks.GetOwnerTokenAsync("resource");

        await Assert.That(token!.Value.Bytes.Span.SequenceEqual(expectedToken)).IsTrue();
        await Assert.That(await client.Locks.ReleaseAsync("resource", token!.Value)).IsTrue();
    }

    [Test]
    public async Task LockCommands_ApplyKeyPrefixesToSetGetAndScripts()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nowner\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            ":1\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");

        await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.FromSeconds(30));
        await client.Locks.GetOwnerTokenAsync("resource");
        await client.Locks.ResetExpiryAsync("resource", "owner", TimeSpan.FromSeconds(45));
        await client.Locks.ReleaseAsync("resource", "owner");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SET tenant:resource owner NX PX 30000",
            "GET tenant:resource",
            "SET tenant:resource owner IFEQ owner PX 45000",
            "DELEX tenant:resource IFEQ owner",
        });
    }

    [Test]
    public async Task LockScripts_FallBackToEvalAndSendLuaKeysAndArgs()
    {
        await using var server = new FakeRespServer(
            NativeLockCommandTests.UnknownDelex,
            NativeLockCommandTests.UnknownDelifeq,
            "-NOSCRIPT No matching script. Please use EVAL.\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsTrue();

        var commands = server.ReceivedCommands;
        await Assert.That(commands[2]).IsEqualTo($"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 resource owner");
        await Assert.That(commands[3]).StartsWith("EVAL ");
        await Assert.That(commands[3]).Contains("redis.call('GET', KEYS[1]) == ARGV[1]");
        await Assert.That(commands[3]).Contains("redis.call('DEL', KEYS[1])");
        await Assert.That(commands[3]).EndsWith(" 1 resource owner");
    }

    [Test]
    public async Task LockCommands_ValidateTokenAndExpiryBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Locks.TryTakeAsync("resource", default(RespireLockToken), TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.TryTakeAsync("resource", "", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.FromTicks(1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Locks.ReleaseAsync("resource", default(RespireLockToken)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.ResetExpiryAsync("resource", "", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.ResetExpiryAsync("resource", "owner", TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task RespireLock_AcquireGeneratesTokenAndDisposeCompareAndDeletes()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var attempt = await client.Locks.AcquireAsync(
            "resource", TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(9_999));
        var mutex = attempt.Lock;

        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(mutex.Key.ToString()).IsEqualTo("resource");
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(mutex.RemainingEstimate).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(mutex.RemainingEstimate).IsLessThanOrEqualTo(mutex.Duration);
        await Assert.That(mutex.ExpiresAtEstimate).IsGreaterThan(DateTimeOffset.UtcNow);
        await Assert.That(mutex.IsReleased).IsFalse();
        var token = mutex.Token.ToUtf8String();
        await Assert.That(token.Length).IsEqualTo(32);

        await mutex.DisposeAsync();

        await Assert.That(RecordedLockOperations(server)).IsEquivalentTo(new[]
        {
            $"SET resource {token} NX PX 30000",
            $"DELEX resource IFEQ {token}",
        });
    }

    [Test]
    public async Task RespireLock_AcquireWithKeepAlive_OwnsRenewalUntilDisposed()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var attempt = await client.Locks.AcquireAsync(
            "resource", TimeSpan.FromSeconds(30), keepAlive: true);
        var mutex = attempt.Lock;
        var keepAliveCancellation = mutex.KeepAliveCancellationToken;

        await Assert.That(keepAliveCancellation.CanBeCanceled).IsTrue();
        await Assert.That(keepAliveCancellation.IsCancellationRequested).IsFalse();
        await Assert.That(async () => await mutex.KeepAliveAsync())
            .Throws<InvalidOperationException>();

        await attempt.DisposeAsync();

        await Assert.That(keepAliveCancellation.IsCancellationRequested).IsTrue();
        await Assert.That(server.ReceivedCommands[^1])
            .StartsWith("DELEX resource IFEQ ");
    }

    [Test]
    public async Task RespireLock_AcquireReturnsUnacquiredAttemptAndIssuesNothingElseWhenTheExists()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var attempt = await client.Locks.AcquireAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(attempt.Acquired).IsFalse();
        await Assert.That(() => _ = attempt.Lock).Throws<RespireLockNotAcquiredException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RespireLock_AcquireOrThrowThrowsWhenTheExists()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Locks.AcquireOrThrowAsync(
                "resource", TimeSpan.FromSeconds(30)))
            .Throws<RespireLockNotAcquiredException>();
    }

    [Test]
    public async Task RespireLock_EachAcquisitionGetsItsOwnToken()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var first = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        var second = await client.Locks.AcquireOrThrowAsync("other", TimeSpan.FromSeconds(30));

        await Assert.That(first.Token == second.Token).IsFalse();
    }

    [Test]
    public async Task RespireLock_SnapshotsByteBackedKeyForHandleOperations()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var key = "resource"u8.ToArray();

        var mutex = await client.Locks.AcquireOrThrowAsync(key, TimeSpan.FromSeconds(30));
        key.AsSpan().Fill((byte)'x');
        await mutex.DisposeAsync();

        var token = mutex.Token.ToUtf8String();
        await Assert.That(RecordedLockOperations(server)).IsEquivalentTo(new[]
        {
            $"SET resource {token} NX PX 30000",
            $"DELEX resource IFEQ {token}",
        });
    }

    [Test]
    public async Task RespireLock_ReleaseAndResetExpiryStopAtTheHandleOnceReleased()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.AlreadyReleased);
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(mutex.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(60))).IsFalse();
        await mutex.DisposeAsync();

        // SET plus one compare-and-DEL: the repeat release, the extend, and dispose never reach the wire.
        await Assert.That(RecordedLockOperations(server).Count).IsEqualTo(2);
    }

    [Test]
    public async Task RespireLock_ConcurrentReleasesShareTheInFlightResult()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        server.DelayCommand("DELEX ", 1000);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        var first = mutex.ReleaseAsync().AsTask();
        await WaitForCommandAsync(server, "DELEX ");
        var second = mutex.ReleaseAsync().AsTask();

        await Assert.That(await first).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(await second).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(RecordedLockOperations(server).Count).IsEqualTo(2);
    }

    [Test]
    public async Task RespireLock_CancelledReleaseConservativelyStopsProtectedWork()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        server.DelayCommand("DELEX ", 500);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        await using var keepAlive = await mutex.KeepAliveAsync();
        using var cancellation = new CancellationTokenSource();

        var release = mutex.ReleaseAsync(cancellation.Token).AsTask();
        await WaitForCommandAsync(server, "DELEX ");
        var dispose = mutex.DisposeAsync().AsTask();
        await cancellation.CancelAsync();

        await Assert.That(async () => await release)
            .Throws<OperationCanceledException>();
        await dispose;
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.NotOwned);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!keepAlive.CancellationToken.IsCancellationRequested)
        {
            await Task.Delay(10, timeout.Token);
        }

        await Assert.That(keepAlive.OwnershipLost).IsTrue();
    }

    [Test]
    public async Task RespireLock_ServerRejectedReleaseRemainsRetryable()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            "-NOPERM release denied\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(async () => await mutex.ReleaseAsync()).Throws<RespireServerException>();
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(async () => await mutex.ReleaseAsync()).Throws<RespireServerException>();
        await Assert.That(RecordedLockOperations(server).Count).IsEqualTo(3);
    }

    [Test]
    public async Task RespireLock_ReleaseCancelledBeforeSubmissionRemainsRetryable()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        await using var keepAlive = await mutex.KeepAliveAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var release = mutex.ReleaseAsync(cancelled.Token).AsTask();
        await Assert.That(async () => await release).Throws<OperationCanceledException>();
        await Assert.That(release.IsCanceled).IsTrue();
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(keepAlive.OwnershipLost).IsFalse();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsFalse();

        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        var token = mutex.Token.ToUtf8String();
        await Assert.That(RecordedLockOperations(server)).IsEquivalentTo(new[]
        {
            $"SET resource {token} NX PX 30000",
            $"DELEX resource IFEQ {token}",
        });
    }

    [Test]
    public async Task RespireLock_ReleaseCancelledWhileWaitingForInflightCapacityRemainsRetryable()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            "+PONG\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.DelayCommand("PING", 1000);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            MaxInflightCommands = 1,
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        await client.EnsureReliableCorrectionOrderingAsync();
        var ping = client.PingAsync().AsTask();
        await WaitForCommandAsync(server, "PING");
        using var cancellation = new CancellationTokenSource();
        var release = mutex.ReleaseAsync(cancellation.Token).AsTask();

        await Task.Delay(50);
        await Assert.That(release.IsCompleted).IsFalse();
        await cancellation.CancelAsync();
        await Assert.That(async () => await release).Throws<OperationCanceledException>();
        await Assert.That(release.IsCanceled).IsTrue();
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("DELEX ", StringComparison.Ordinal)))
            .IsFalse();

        await ping;
        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task CommandCancelledWhileWaitingForCapacityStillSurfacesCallerCancellation()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => LockReply(command),
        };
        server.DelayCommand("PING", 1000);
        await using var client = await ConnectWithSingleInflightSlotAsync(server);
        var ping = client.PingAsync().AsTask();
        await WaitForCommandAsync(server, "PING");
        using var cancellation = new CancellationTokenSource();

        // A non-lock command parked on the full in-flight ring sees an ordinary cancellation:
        // catch (OperationCanceledException) matches and the caller's token is preserved.
        var get = client.Strings.GetAsync<string>("resource", cancellation.Token).AsTask();
        await Task.Delay(50);
        await Assert.That(get.IsCompleted).IsFalse();
        await cancellation.CancelAsync();

        OperationCanceledException? caught = null;
        try
        {
            await get.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(get.IsCanceled).IsTrue();
        await ping;
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task RespireLock_ReleaseTimeoutWhileWaitingForInflightCapacityRemainsRetryable()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            "+PONG\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.DelayCommand("PING", 3000);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            MaxInflightCommands = 1,
            CommandTimeout = TimeSpan.FromSeconds(1),
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        await client.EnsureReliableCorrectionOrderingAsync();
        var ping = client.PingAsync().AsTask();
        await WaitForCommandAsync(server, "PING");
        await Task.Delay(900);

        await Assert.That(async () => await mutex.ReleaseAsync()).Throws<RespireTimeoutException>();
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("DELEX ", StringComparison.Ordinal)))
            .IsFalse();

        await Assert.That(async () => await ping).Throws<RespireTimeoutException>();
        await Task.Delay(2200);
        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task RespireLock_ReleaseWithoutClientPermissionsUsesCompatibleDelete()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "-NOPERM this user has no permissions to run the 'client|id' command\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        var token = mutex.Token.ToUtf8String();
        await Assert.That(RecordedLockOperations(server)).IsEquivalentTo(new[]
        {
            $"SET resource {token} NX PX 30000",
            $"DELEX resource IFEQ {token}",
        });
    }

    [Test]
    public async Task RespireLock_UnfencedReleaseFallbackIsLoggedOncePerClient()
    {
        var logger = new CapturingLoggerFactory();
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLIENT ID"
                ? "-NOPERM this user has no permissions to run the 'client|id' command\r\n"u8.ToArray()
                : LockReply(command),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            LoggerFactory = logger,
        });
        using var cancellation = new CancellationTokenSource();

        // A cancellable token makes the release fenceable, so CLIENT ID is attempted and denied.
        var first = await client.Locks.AcquireOrThrowAsync("first", TimeSpan.FromSeconds(30));
        await Assert.That(await first.ReleaseAsync(cancellation.Token)).IsEqualTo(LockReleaseOutcome.Released);
        var second = await client.Locks.AcquireOrThrowAsync("second", TimeSpan.FromSeconds(30));
        await Assert.That(await second.ReleaseAsync(cancellation.Token)).IsEqualTo(LockReleaseOutcome.Released);

        await Assert.That(DelexCount(server)).IsEqualTo(2);
        await Assert.That(logger.Warnings.Count(warning => warning is RespireServerException)).IsEqualTo(1);
    }

    [Test]
    public async Task RespireLock_ReleaseWithUnexpectedClientIdErrorDoesNotFallBack()
    {
        // Only permission or unsupported-command replies select the unfenced release; any other
        // CLIENT ID failure surfaces before a delete is submitted and leaves the lock retryable.
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLIENT ID"
                ? "-ERR CLIENT ID failed\r\n"u8.ToArray()
                : LockReply(command),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = TimeSpan.FromSeconds(5),
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(async () => await mutex.ReleaseAsync()).Throws<RespireServerException>();
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(DelexCount(server)).IsEqualTo(0);
    }

    [Test]
    public async Task RespireLock_JoinerRetriesWhenStarterCancelsBeforeSubmission()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => LockReply(command),
        };
        server.DelayCommand("PING", 1000);
        await using var client = await ConnectWithSingleInflightSlotAsync(server);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        await client.EnsureReliableCorrectionOrderingAsync();
        var ping = client.PingAsync().AsTask();
        await WaitForCommandAsync(server, "PING");
        using var starterCancellation = new CancellationTokenSource();

        // The starter parks on the full in-flight ring, before its delete is enqueued.
        var starter = mutex.ReleaseAsync(starterCancellation.Token).AsTask();
        var joiner = mutex.ReleaseAsync().AsTask();
        await Task.Delay(50);
        await Assert.That(starter.IsCompleted).IsFalse();
        await Assert.That(joiner.IsCompleted).IsFalse();
        await starterCancellation.CancelAsync();

        await Assert.That(async () => await starter).Throws<OperationCanceledException>();
        await Assert.That(await joiner.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(LockReleaseOutcome.Released);
        await ping;
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(DelexCount(server)).IsEqualTo(1);
    }

    [Test]
    public async Task RespireLock_ConcurrentJoinersAllRetryAfterStarterCancelsBeforeSubmission()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => LockReply(command),
        };
        server.DelayCommand("PING", 1000);
        await using var client = await ConnectWithSingleInflightSlotAsync(server);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        await client.EnsureReliableCorrectionOrderingAsync();
        var ping = client.PingAsync().AsTask();
        await WaitForCommandAsync(server, "PING");
        using var starterCancellation = new CancellationTokenSource();

        var starter = mutex.ReleaseAsync(starterCancellation.Token).AsTask();
        var joiners = new[] { mutex.ReleaseAsync().AsTask(), mutex.ReleaseAsync().AsTask(), mutex.ReleaseAsync().AsTask() };
        await Task.Delay(50);
        await starterCancellation.CancelAsync();

        await Assert.That(async () => await starter).Throws<OperationCanceledException>();
        // Whichever joiner restarts the release first, the others follow that attempt or see
        // its result; none inherits the starter's cancellation.
        var outcomes = await Task.WhenAll(joiners).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(outcomes.Count(outcome => outcome == LockReleaseOutcome.Released)).IsGreaterThanOrEqualTo(1);
        await Assert.That(outcomes.All(outcome => outcome is LockReleaseOutcome.Released or LockReleaseOutcome.AlreadyReleased))
            .IsTrue();
        await ping;
        await Assert.That(DelexCount(server)).IsEqualTo(1);
    }

    [Test]
    public async Task RespireLock_JoinerCancellationEndsOnlyItsOwnWait()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => LockReply(command),
        };
        server.DelayCommand("DELEX ", 500);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        var starter = mutex.ReleaseAsync().AsTask();
        await WaitForCommandAsync(server, "DELEX ");
        using var joinerCancellation = new CancellationTokenSource();
        var joiner = mutex.ReleaseAsync(joinerCancellation.Token).AsTask();
        await joinerCancellation.CancelAsync();

        await Assert.That(async () => await joiner.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(starter.IsCompleted).IsFalse();
        await Assert.That(await starter.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(DelexCount(server)).IsEqualTo(1);
    }

    [Test]
    public async Task RespireLock_FenceFailureAfterUncertainReleaseIsLoggedWithoutMaskingTheError()
    {
        var logger = new CapturingLoggerFactory();
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLIENT KILL ID 41"
                ? "-ERR fence denied\r\n"u8.ToArray()
                : LockReply(command),
        };
        server.DelayCommand("DELEX ", 1000);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            LoggerFactory = logger,
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        var release = mutex.ReleaseAsync(cancellation.Token).AsTask();
        await WaitForCommandAsync(server, "DELEX ");
        await cancellation.CancelAsync();

        // The caller sees its own cancellation, not the fence's server error.
        await Assert.That(async () => await release.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(server.ReceivedCommands.Contains("CLIENT KILL ID 41")).IsTrue();
        await Assert.That(logger.Warnings.Any(warning => warning is RespireServerException)).IsTrue();
    }

    [Test]
    public async Task RespireLock_ReleaseCancelledWhileOpeningRedirectTargetRemainsRetryable()
    {
        var targetAccept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(4, targetAccept.Task, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => LockReply(command),
        };
        await using var seed = new FakeRespServer(8, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n");
        var moved = Encoding.ASCII.GetBytes(
            $"-MOVED {Respire.Internal.ClusterHash.GetSlot("resource")} 127.0.0.1:{target.Port}\r\n");
        var seedDeletes = 0;
        seed.ReplyOverride = (_, command) =>
            command.StartsWith("CLUSTER ", StringComparison.Ordinal) ? topology
            : command.StartsWith("DELEX ", StringComparison.Ordinal)
                ? Interlocked.Increment(ref seedDeletes) == 1 ? moved : ":1\r\n"u8.ToArray()
            : LockReply(command);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        // MOVED definitively rejects the first delete; the redirect target never answers its
        // connection setup, so cancellation lands before any delete reaches it.
        var release = mutex.ReleaseAsync(cancellation.Token).AsTask();
        await WaitForCommandAsync(seed, "DELEX ");
        await Task.Delay(100);
        await Assert.That(release.IsCompleted).IsFalse();
        await cancellation.CancelAsync();

        await Assert.That(async () => await release.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(DelexCount(target)).IsEqualTo(0);

        targetAccept.TrySetResult();
        await Assert.That(await mutex.ReleaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            .IsEqualTo(LockReleaseOutcome.Released);
    }

    [Test]
    public async Task RespireLock_ReleaseFallsBackUnfencedWhenAskTargetDeniesClientId()
    {
        var logger = new CapturingLoggerFactory();
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLIENT ID"
                ? "-NOPERM this user has no permissions to run the 'client|id' command\r\n"u8.ToArray()
                : LockReply(command),
        };
        await using var seed = new FakeRespServer(8, FakeRespServer.OkReply);
        ConfigureAskingSeed(seed, target);
        await using var client = await ConnectClusterAsync(seed, logger);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        // The cancellable token makes the release fenceable. The seed answers its delete with
        // ASK, and the target denies CLIENT ID, so the delete continues there unfenced.
        await Assert.That(await mutex.ReleaseAsync(cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            .IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(DelexCount(seed)).IsEqualTo(1);
        await Assert.That(DelexCount(target)).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands).Contains("ASKING");
        await Assert.That(logger.Warnings.Count(warning => warning is RespireServerException)).IsEqualTo(1);
    }

    [Test]
    public async Task RespireLock_ReleaseDoesNotFallBackWhenAskTargetFailsClientIdUnexpectedly()
    {
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLIENT ID"
                ? "-ERR CLIENT ID failed\r\n"u8.ToArray()
                : LockReply(command),
        };
        await using var seed = new FakeRespServer(8, FakeRespServer.OkReply);
        ConfigureAskingSeed(seed, target);
        await using var client = await ConnectClusterAsync(seed, null);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        // Only a definitive denial selects the unfenced delete. Any other failure surfaces with
        // nothing sent to the target, so the release stays retryable.
        await Assert.That(async () => await mutex.ReleaseAsync(cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            .Throws<RespireServerException>();
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(DelexCount(target)).IsEqualTo(0);
    }

    private static void ConfigureAskingSeed(FakeRespServer seed, FakeRespServer target)
    {
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n");
        var ask = Encoding.ASCII.GetBytes(
            $"-ASK {Respire.Internal.ClusterHash.GetSlot("resource")} 127.0.0.1:{target.Port}\r\n");
        seed.ReplyOverride = (_, command) =>
            command.StartsWith("CLUSTER ", StringComparison.Ordinal) ? topology
            : command.StartsWith("DELEX ", StringComparison.Ordinal) ? ask
            : LockReply(command);
    }

    private static ValueTask<RespireClient> ConnectClusterAsync(FakeRespServer seed, ILoggerFactory? logger)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            LoggerFactory = logger,
        });

    [Test]
    public async Task RespireLock_ResetExpiryRecordsDurationAndStopsAfterOwnershipLoss()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$-1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = null,
        });

        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(await mutex.ResetExpiryAsync(
            TimeSpan.FromSeconds(45) + TimeSpan.FromTicks(9_999))).IsTrue();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromMilliseconds(45_001));

        await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(90))).IsFalse();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromMilliseconds(45_001));
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(90))).IsFalse();
        await Assert.That(server.ReceivedCommands).Count().IsEqualTo(5);
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("CLIENT ID");
        await Assert.That(server.ReceivedCommands[3]).EndsWith(" 45001");
    }

    [Test]
    public async Task RespireLock_NoTimeoutExpiryResetConnectionLossMarksOwnershipLost()
    {
        await using var server = new FakeRespServer(
            3,
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.CloseConnectionAfterCommand = 4;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = null,
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(async () => await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();

        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(server.ReceivedCommands).Contains("CLIENT ID");
        await Assert.That(server.ReceivedCommands.Any(
            command => command.StartsWith("CLIENT KILL ID 41", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RespireLock_ConcurrentExpiryResetsPublishMetadataInRequestOrder()
    {
        var commands = new CoordinatedLockCommands();
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());

        var first = mutex.ResetExpiryAsync(TimeSpan.FromSeconds(45)).AsTask();
        await commands.FirstExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = mutex.ResetExpiryAsync(TimeSpan.FromSeconds(90)).AsTask();

        await Assert.That(commands.ExtensionCount).IsEqualTo(1);
        commands.CompleteFirstExtension();

        await Assert.That(await first).IsTrue();
        await Assert.That(await second).IsTrue();
        await Assert.That(commands.Expiries).IsEquivalentTo(
            [TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)]);
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromSeconds(90));
    }

    [Test]
    public async Task RespireLock_CancelledExpiryResetIsFencedAndMarksTheHandleNotOwned()
    {
        await using var server = new FakeRespServer(
            3,
            ":41\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            ":1\r\n"u8.ToArray());
        server.DelayReply(3, 500);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        });
        await client.EnsureReliableCorrectionOrderingAsync();
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () =>
                await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(60), cancellation.Token))
            .Throws<OperationCanceledException>();
        await WaitForCommandsAsync(server, 6);
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(10))).IsFalse();

        var extensionIndexes = server.ReceivedCommands
            .Select((command, index) => (command, index))
            .Where(item => item.command.Contains(" IFEQ ", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        var fenceIndex = server.ReceivedCommands
            .Select((command, index) => (command, index))
            .Single(item => item.command == "CLIENT KILL ID 41")
            .index;

        await Assert.That(extensionIndexes).Count().IsEqualTo(1);
        await Assert.That(fenceIndex).IsGreaterThan(extensionIndexes[0]);
        await mutex.DisposeAsync();
    }

    [Test]
    public async Task RespireLock_QueuedRenewalUsesTheLatestDuration()
    {
        var commands = new CoordinatedLockCommands();
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());

        var extension = mutex.ResetExpiryAsync(TimeSpan.FromMinutes(5)).AsTask();
        await commands.FirstExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var renewal = mutex.RenewAsync(static () => { }, CancellationToken.None).AsTask();

        await Assert.That(commands.ExtensionCount).IsEqualTo(1);
        commands.CompleteFirstExtension();

        await Assert.That(await extension).IsTrue();
        await Assert.That(await renewal).IsTrue();
        await Assert.That(commands.Expiries).IsEquivalentTo(
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)]);
    }

    [Test]
    public async Task RespireLock_KeepAliveCancelsWhenOwnershipIsLost()
    {
        await using var server = CreateKeepAliveOwnershipLossServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromMilliseconds(200));

        await using var keepAlive = await mutex.KeepAliveAsync();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), keepAlive.CancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(keepAlive.CancellationToken.IsCancellationRequested).IsTrue();
        await Assert.That(keepAlive.OwnershipLost).IsTrue();
        await Assert.That(keepAlive.Failure).IsNull();
        await Assert.That(mutex.IsReleased).IsTrue();
    }

    [Test]
    public async Task RespireLock_KeepAliveDeadlineWaitsForAcknowledgedFenceBeforeDisposal()
    {
        await using var server = CreateKeepAliveOwnershipLossServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(5));
        await client.EnsureReliableCorrectionOrderingAsync();

        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fenceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command.Contains(" IFEQ ", StringComparison.Ordinal))
            {
                renewalStarted.TrySetResult();
                return true;
            }

            if (command == "CLIENT KILL ID 41")
            {
                fenceStarted.TrySetResult();
                return true;
            }

            return false;
        };

        var keepAlive = await mutex.KeepAliveAsync();
        Task? disposal = null;
        try
        {
            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(keepAlive.CancellationToken.IsCancellationRequested).IsTrue();
            await Assert.That(keepAlive.OwnershipLost).IsTrue();
            await Assert.That(mutex.IsReleased).IsTrue();

            disposal = keepAlive.DisposeAsync().AsTask();
            var observation = Task.Delay(TimeSpan.FromMilliseconds(100));
            await Assert.That(await Task.WhenAny(disposal, observation)).IsSameReferenceAs(observation);
            var fenceIndex = server.ReceivedCommands.ToList().IndexOf("CLIENT KILL ID 41");
            await Assert.That(fenceIndex).IsGreaterThanOrEqualTo(0);
            var controlConnection = server.ReceivedConnectionIds[fenceIndex];
            await Assert.That(controlConnection).IsEqualTo(1);
            await server.SendRawAsync(":1\r\n"u8.ToArray(), controlConnection);
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(keepAlive.Failure).IsNull();
        }
        finally
        {
            // A failed assertion must close the control socket before awaiting the loop.
            // Disposing the keepalive first would itself wait for the missing fence reply.
            await client.DisposeAsync();
            await (disposal ?? keepAlive.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static FakeRespServer CreateKeepAliveOwnershipLossServer()
        // A renewal can cross its lease deadline after acceptance. The resulting fence uses
        // a separate connection, which the fixture must accept even when renewal replies fail.
        => new(2, FakeRespServer.OkReply, ":41\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), "$-1\r\n"u8.ToArray());

    [Test]
    public async Task RespireLock_ManualShorteningReschedulesKeepAlive()
    {
        var commands = new CoordinatedLockCommands(blockFirstExtension: false);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromMinutes(1),
            Stopwatch.GetTimestamp());

        // Keep the shortened lease long enough for parallel CI scheduling. The 10-second
        // observation window still expires before the original 30-second renewal delay.
        await using var keepAlive = await mutex.KeepAliveAsync();
        await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(5))).IsTrue();

        await commands.SecondExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(commands.Expiries).IsEquivalentTo(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)]);
    }

    [Test]
    public async Task RespireLock_KeepAliveCancelsAtLeaseDeadlineWhileRenewalIsInFlight()
    {
        var commands = new CoordinatedLockCommands(waitForCancellation: true);
        // Renewal starts halfway through the lease. Leave enough scheduling time for the
        // parallel suite to enter the blocked renewal before the deadline expires.
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromSeconds(5),
            Stopwatch.GetTimestamp());
        await using var keepAlive = await mutex.KeepAliveAsync();

        await commands.FirstExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, keepAlive.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(keepAlive.OwnershipLost).IsTrue();
        await Assert.That(mutex.IsReleased).IsTrue();
    }

    [Test]
    public async Task RespireLock_KeepAliveCancelsAtLeaseDeadlineWhileReleaseIsInFlight()
    {
        var commands = new CoordinatedLockCommands(waitForRelease: true);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromSeconds(1),
            Stopwatch.GetTimestamp());
        var keepAlive = await mutex.KeepAliveAsync();
        var release = mutex.ReleaseAsync().AsTask();

        try
        {
            await commands.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(Timeout.InfiniteTimeSpan, keepAlive.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            commands.CompletePendingRelease();
            await keepAlive.DisposeAsync();
        }

        await release;
        await Assert.That(keepAlive.OwnershipLost).IsTrue();
        await Assert.That(mutex.IsReleased).IsTrue();
    }

    [Test]
    public async Task RespireLock_KeepAliveSurvivesReleaseThatStartsDuringRenewalAndIsNotSubmitted()
    {
        var commands = new CoordinatedLockCommands(waitForRelease: true);
        var clock = new GatedLockClock();
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromMilliseconds(500),
            clock.GetTimestamp(),
            clock);
        var keepAlive = await mutex.KeepAliveAsync();
        try
        {
            var renewal = await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A manual reset holds the extension gate, so the keep-alive renewal queues behind it.
            var manualReset = mutex.ResetExpiryAsync(TimeSpan.FromMilliseconds(500)).AsTask();
            await commands.FirstExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            renewal.Fire();
            await Task.Delay(100);

            // The release starts after the keep-alive read the lease, so its renewal stops at
            // the handle instead of reaching Redis.
            var release = mutex.ReleaseAsync().AsTask();
            await commands.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            commands.CompleteFirstExtension();
            await Assert.That(await manualReset.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
            await Task.Delay(100);
            await Assert.That(keepAlive.OwnershipLost).IsFalse();

            commands.RejectPendingReleaseBeforeSubmission();
            await Assert.That(async () => await release.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Task.Delay(100);

            await Assert.That(mutex.IsReleased).IsFalse();
            await Assert.That(keepAlive.OwnershipLost).IsFalse();
            await Assert.That(keepAlive.CancellationToken.IsCancellationRequested).IsFalse();
        }
        finally
        {
            commands.CompletePendingRelease();
            await keepAlive.DisposeAsync();
        }
    }

    [Test]
    public async Task RespireLock_KeepAliveLeaseSnapshotReportsReleaseInsteadOfElapsedLease()
    {
        var commands = new CoordinatedLockCommands(waitForRelease: true);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());

        var heldPhase = mutex.GetKeepAlivePhase(out var held);
        await Assert.That(heldPhase).IsEqualTo(RespireLock.KeepAlivePhase.Held);
        await Assert.That(held).IsGreaterThan(TimeSpan.Zero);

        var release = mutex.ReleaseAsync().AsTask();
        await commands.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(mutex.GetKeepAlivePhase(out _)).IsEqualTo(RespireLock.KeepAlivePhase.Releasing);

        commands.CompletePendingRelease();
        await Assert.That(await release).IsEqualTo(LockReleaseOutcome.Released);
        var afterPhase = mutex.GetKeepAlivePhase(out var afterRelease);
        await Assert.That(afterPhase).IsEqualTo(RespireLock.KeepAlivePhase.Ended);
        await Assert.That(afterRelease).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task RespireLock_StoppingKeepAliveWaitsForRenewalAndPreservesRelease()
    {
        var commands = new CoordinatedLockCommands();
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromMilliseconds(500),
            Stopwatch.GetTimestamp());
        var keepAlive = await mutex.KeepAliveAsync();

        await commands.FirstExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = keepAlive.DisposeAsync().AsTask();
        await Assert.That(dispose.IsCompleted).IsFalse();
        commands.CompleteFirstExtension();
        await dispose;

        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
    }

    [Test]
    public async Task RespireLock_RoundsSubMillisecondExpiryUp()
    {
        var commands = new CoordinatedLockCommands(blockFirstExtension: false);
        var mutex = new RespireLock(commands, "resource", "owner", TimeSpan.FromMinutes(1), Stopwatch.GetTimestamp());

        await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1)))
            .IsTrue();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromMilliseconds(2));
    }

    [Test]
    [Arguments(1L)]
    [Arguments(45_000L)]
    public async Task RespireLock_WholeMillisecondExpiryIsNotRoundedUp(long milliseconds)
    {
        var commands = new CoordinatedLockCommands(blockFirstExtension: false);
        var mutex = new RespireLock(commands, "resource", "owner", TimeSpan.FromMinutes(1), Stopwatch.GetTimestamp());

        await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromMilliseconds(milliseconds))).IsTrue();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));
    }

    [Test]
    public async Task RespireLock_SubMillisecondExpiryRoundsUpToOneMillisecond()
    {
        var commands = new CoordinatedLockCommands(blockFirstExtension: false);
        var mutex = new RespireLock(commands, "resource", "owner", TimeSpan.FromMinutes(1), Stopwatch.GetTimestamp());

        await Assert.That(await mutex.ResetExpiryAsync(TimeSpan.FromTicks(1))).IsTrue();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromMilliseconds(1));
    }

    [Test]
    public async Task RespireLock_UncertainRenewalCancelsProtectedWorkBeforeFenceCompletes()
    {
        var commands = new CoordinatedLockCommands(reportUncertain: true);
        var clock = new GatedLockClock();
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromMilliseconds(500),
            clock.GetTimestamp(),
            clock);
        var keepAlive = await mutex.KeepAliveAsync();
        try
        {
            var renewal = await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(renewal.DueTime).IsEqualTo(TimeSpan.FromMilliseconds(250));
            await Assert.That(commands.FenceStarted.Task.IsCompleted).IsFalse();
            // Scheduling cannot consume the lease before this test releases the renewal timer.
            renewal.Fire();
            await commands.FenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(keepAlive.CancellationToken.IsCancellationRequested).IsTrue();
            await Assert.That(keepAlive.OwnershipLost).IsTrue();
            await Assert.That(commands.FenceCompleted.Task.IsCompleted).IsFalse();
        }
        finally
        {
            commands.CompleteFence();
            await keepAlive.DisposeAsync();
        }
        await Assert.That(keepAlive.Failure).IsTypeOf<RespireConnectionException>();
    }

    [Test]
    public async Task RespireLock_UncertainManualResetCancelsSleepingKeepAliveBeforeFenceCompletes()
    {
        var commands = new CoordinatedLockCommands(reportUncertain: true);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());
        var keepAlive = await mutex.KeepAliveAsync();

        var extension = mutex.ResetExpiryAsync(TimeSpan.FromSeconds(5)).AsTask();
        await commands.FenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, keepAlive.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        await Assert.That(keepAlive.OwnershipLost).IsTrue();
        await Assert.That(commands.FenceCompleted.Task.IsCompleted).IsFalse();

        commands.CompleteFence();
        await Assert.That(async () => await extension).Throws<RespireConnectionException>();
        await keepAlive.DisposeAsync();
    }

    [Test]
    public async Task RespireLock_OwnershipLossSurvivesConcurrentReleaseRejection()
    {
        var commands = new CoordinatedLockCommands(raceOwnershipLoss: true);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner",
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());

        var extension = mutex.ResetExpiryAsync(TimeSpan.FromSeconds(60)).AsTask();
        await commands.RaceExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var release = mutex.ReleaseAsync().AsTask();
        await commands.RaceReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        commands.CompleteRaceExtension();
        await Assert.That(await extension).IsFalse();
        commands.CompleteRaceRelease();
        await Assert.That(async () => await release).Throws<RespireServerException>();

        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.NotOwned);
    }

    [Test]
    public async Task RespireLock_ReleaseCancelsSleepingKeepAlive()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        await using var keepAlive = await mutex.KeepAliveAsync();

        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!keepAlive.CancellationToken.IsCancellationRequested)
        {
            await Task.Delay(10, timeout.Token);
        }

        await Assert.That(keepAlive.OwnershipLost).IsTrue();
    }

    [Test]
    public async Task KeepAliveDelayAccountsForElapsedLeaseTime()
    {
        await Assert.That(RespireLockKeepAlive.GetRenewalDelay(
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(25)))
            .IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(RespireLockKeepAlive.GetRenewalDelay(
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15)))
            .IsEqualTo(TimeSpan.FromMilliseconds(10));
        await Assert.That(RespireLockKeepAlive.GetRenewalDelay(
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)))
            .IsEqualTo(TimeSpan.FromMilliseconds(10));
        await Assert.That(RespireLockKeepAlive.GetRenewalDelay(
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)))
            .IsEqualTo(TimeSpan.FromMilliseconds(5));
        await Assert.That(RespireLockKeepAlive.GetRenewalDelay(
                TimeSpan.FromTicks(1), TimeSpan.FromTicks(1)))
            .IsEqualTo(TimeSpan.FromTicks(1));
        await Assert.That(RespireLockKeepAlive.GetTimerDelayChunk(TimeSpan.MaxValue))
            .IsEqualTo(TimeSpan.FromMilliseconds(uint.MaxValue - 1d));
    }

    [Test]
    public async Task RespireLock_AcquireAppliesTheKeyPrefixToBothTheSetAndTheReleaseScript()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");

        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        var token = mutex.Token.ToUtf8String();
        await mutex.DisposeAsync();

        await Assert.That(RecordedLockOperations(server)).IsEquivalentTo(new[]
        {
            $"SET tenant:resource {token} NX PX 30000",
            $"DELEX tenant:resource IFEQ {token}",
        });
    }

    [Test]
    public async Task RespireLock_OwnershipCheckUsesTheAcquiringPrefix()
    {
        var ownerReply = new byte[39];
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, ownerReply, ":1\r\n"u8.ToArray());
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefixed = root.WithKeyPrefix("tenant:");
        var mutex = await prefixed.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        Encoding.ASCII.GetBytes($"$32\r\n{mutex.Token.ToUtf8String()}\r\n")
            .CopyTo(ownerReply, 0);

        await Assert.That(await mutex.VerifyStillHeldAsync()).IsTrue();
        await mutex.DisposeAsync();

        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("GET tenant:resource");
    }

    [Test]
    [Arguments(0)]
    [Arguments(150)]
    public async Task RespireLock_PollingAcquireReturnsUnacquiredAfterTheWaitBudgetIsSpent(int firstReplyDelay)
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        server.DelayReply(0, firstReplyDelay);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var wait = TimeSpan.FromMilliseconds(120);
        var started = Stopwatch.GetTimestamp();
        var attempt = await client.Locks.AcquireAsync(
            "resource",
            TimeSpan.FromSeconds(30),
            wait: wait,
            retryEvery: TimeSpan.FromMilliseconds(50));

        await Assert.That(attempt.Acquired).IsFalse();
        await Assert.That(Stopwatch.GetElapsedTime(started)).IsGreaterThanOrEqualTo(wait);
        await Assert.That(server.ReceivedCommands.Count).IsGreaterThanOrEqualTo(1);
        if (firstReplyDelay > wait.TotalMilliseconds)
            await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RespireLock_PollingAcquireRetriesWithAnExplicitInterval()
    {
        // Prove retry separately: a loaded runner may spend the entire short wait budget
        // on its first network response, which is a valid unsuccessful acquisition.
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray(), FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var attempt = await client.Locks.AcquireAsync(
            "resource", TimeSpan.FromSeconds(30), wait: TimeSpan.FromSeconds(5),
            retryEvery: TimeSpan.FromMilliseconds(50), cancellationToken: timeout.Token);

        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RespireLock_PollingAcquireDefaultsTheRetryInterval()
    {
        // The second reply grants the lock. Prove that omitting retryEvery still retries,
        // without requiring two network round trips inside a 120 ms scheduling window.
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray(), FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var attempt = await client.Locks.AcquireAsync(
            "resource", TimeSpan.FromSeconds(30), wait: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);

        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RespireLock_PollingAcquireUsesShortRemainingBudgetForFinalAttempt()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var mutex = await client.Locks.AcquireOrThrowAsync(
            "resource",
            TimeSpan.FromSeconds(30),
            // Keep the wait below the retry interval while allowing the first loopback response
            // enough headroom on loaded CI runners.
            wait: TimeSpan.FromMilliseconds(500),
            retryEvery: TimeSpan.FromSeconds(5));

        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RespireLock_PollingAcquireValidatesItsWaitArguments()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Locks.AcquireAsync(
                "resource", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(-1), TimeSpan.FromMilliseconds(50)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Locks.AcquireAsync(
                "resource", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    private static async Task WaitForCommandsAsync(FakeRespServer server, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    // Replies keyed by command text, so tests do not depend on how many setup commands run first.
    // Null keeps the scripted reply (+OK).
    private static byte[]? LockReply(string command)
    {
        if (command == "CLIENT ID") return ":41\r\n"u8.ToArray();
        if (command.StartsWith("CLIENT KILL ", StringComparison.Ordinal)) return ":0\r\n"u8.ToArray();
        if (command == "PING") return "+PONG\r\n"u8.ToArray();
        if (command.StartsWith("DELEX ", StringComparison.Ordinal)) return ":1\r\n"u8.ToArray();
        return null;
    }

    private static int DelexCount(FakeRespServer server)
        => server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal));

    // One connection with one in-flight slot: a parked PING keeps later commands waiting for
    // capacity, which is before they are enqueued.
    private static ValueTask<RespireClient> ConnectWithSingleInflightSlotAsync(FakeRespServer server)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            MaxInflightCommands = 1,
        });

    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        private readonly List<Exception?> _warnings = [];

        public IReadOnlyList<Exception?> Warnings
        {
            get { lock (_warnings) return _warnings.ToArray(); }
        }

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            lock (_warnings) _warnings.Add(exception);
        }
    }

    private static IReadOnlyList<string> RecordedLockOperations(FakeRespServer server)
        // Drop only correction-ordering setup, so an unexpected fallback write still fails assertions.
        => server.ReceivedCommands.Where(command => !command.StartsWith("CLIENT ", StringComparison.Ordinal)).ToArray();

    private static async Task WaitForCommandAsync(FakeRespServer server, string prefix)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Any(command => command.StartsWith(prefix, StringComparison.Ordinal)))
            await Task.Delay(10, timeout.Token);
    }

    private sealed class GatedLockClock : TimeProvider
    {
        // This case tests uncertainty ordering, not elapsed-time expiry. Only explicit timer
        // release advances the loop; a busy runner cannot expire the lease before renewal.
        public override long GetTimestamp() => 0;
        internal TaskCompletionSource<LockTimer> Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new LockTimer(callback, state, dueTime);
            Scheduled.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class LockTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private TimerCallback? _callback = callback;
        internal TimeSpan DueTime => dueTime;
        internal void Fire() => Interlocked.Exchange(ref _callback, null)?.Invoke(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("This test clock supports one-shot delays only.");
        public void Dispose() => Interlocked.Exchange(ref _callback, null);
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }

    private sealed class CoordinatedLockCommands : ILockCommands, IManagedLockCommands
    {
        private readonly bool _blockFirstExtension;
        private readonly bool _reportUncertain;
        private readonly bool _raceOwnershipLoss;
        private readonly bool _waitForCancellation;
        private readonly bool _waitForRelease;
        private readonly TaskCompletionSource<bool> _pendingRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstExtension =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fence =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _raceExtension =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _raceRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TimeSpan> _expiries = [];

        public CoordinatedLockCommands(
            bool blockFirstExtension = true,
            bool reportUncertain = false,
            bool raceOwnershipLoss = false,
            bool waitForCancellation = false,
            bool waitForRelease = false)
        {
            _blockFirstExtension = blockFirstExtension;
            _reportUncertain = reportUncertain;
            _raceOwnershipLoss = raceOwnershipLoss;
            _waitForCancellation = waitForCancellation;
            _waitForRelease = waitForRelease;
        }

        public TaskCompletionSource FirstExtensionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondExtensionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FenceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FenceCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RaceExtensionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RaceReleaseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExtensionCount
        {
            get
            {
                lock (_expiries)
                {
                    return _expiries.Count;
                }
            }
        }

        public IReadOnlyList<TimeSpan> Expiries
        {
            get
            {
                lock (_expiries)
                {
                    return _expiries.ToArray();
                }
            }
        }

        public void CompleteFirstExtension() => _firstExtension.TrySetResult(true);

        public void CompleteFence() => _fence.TrySetResult();

        public void CompletePendingRelease() => _pendingRelease.TrySetResult(true);

        // What the managed release reports when its delete was cancelled before submission.
        public void RejectPendingReleaseBeforeSubmission()
            => _pendingRelease.TrySetException(new LockReleaseNotSubmittedException(new OperationCanceledException()));

        public void CompleteRaceExtension() => _raceExtension.TrySetResult();

        public void CompleteRaceRelease() => _raceRelease.TrySetResult();

        ValueTask<bool> IManagedLockCommands.ReleaseManagedAsync(
            RespireKey key,
            RespireLockToken token,
            Action onOutcomeUncertain,
            CancellationToken cancellationToken)
            => ReleaseAsync(key, token, cancellationToken);

        async ValueTask<bool> IManagedLockCommands.ExtendManagedAsync(
            RespireKey key,
            RespireLockToken token,
            TimeSpan expiry,
            Action? onOutcomeUncertain,
            CancellationToken cancellationToken)
        {
            if (_waitForCancellation)
            {
                FirstExtensionStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_raceOwnershipLoss)
            {
                RaceExtensionStarted.TrySetResult();
                await _raceExtension.Task;
                return false;
            }

            if (!_reportUncertain)
            {
                return await ResetExpiryAsync(key, token, expiry, cancellationToken);
            }

            onOutcomeUncertain?.Invoke();
            FenceStarted.TrySetResult();
            await _fence.Task;
            FenceCompleted.TrySetResult();
            throw new RespireConnectionException("renewal outcome is uncertain");
        }

        public ValueTask<bool> ResetExpiryAsync(
            RespireKey key,
            RespireLockToken token,
            TimeSpan expiry,
            CancellationToken cancellationToken = default)
        {
            int call;
            lock (_expiries)
            {
                _expiries.Add(expiry);
                call = _expiries.Count;
            }

            if (call == 1)
            {
                FirstExtensionStarted.TrySetResult();
                return _blockFirstExtension
                    ? new ValueTask<bool>(_firstExtension.Task)
                    : ValueTask.FromResult(true);
            }

            SecondExtensionStarted.TrySetResult();

            return ValueTask.FromResult(true);
        }

        public ValueTask<RespireLockAttempt> AcquireAsync(
            RespireKey key,
            TimeSpan expiry,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<RespireLockAttempt> AcquireAsync(
            RespireKey key,
            TimeSpan expiry,
            TimeSpan wait,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<RespireLockAttempt> AcquireAsync(
            RespireKey key,
            TimeSpan expiry,
            TimeSpan wait,
            TimeSpan retryEvery,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<RespireLock> AcquireOrThrowAsync(
            RespireKey key,
            TimeSpan expiry,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<RespireLock> AcquireOrThrowAsync(
            RespireKey key,
            TimeSpan expiry,
            TimeSpan wait,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<RespireLock> AcquireOrThrowAsync(
            RespireKey key,
            TimeSpan expiry,
            TimeSpan wait,
            TimeSpan retryEvery,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<bool> TryTakeAsync(
            RespireKey key,
            RespireLockToken token,
            TimeSpan expiry,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async ValueTask<bool> ReleaseAsync(
            RespireKey key,
            RespireLockToken token,
            CancellationToken cancellationToken = default)
        {
            if (_waitForRelease)
            {
                ReleaseStarted.TrySetResult();
                return await _pendingRelease.Task;
            }

            if (!_raceOwnershipLoss)
            {
                return true;
            }

            RaceReleaseStarted.TrySetResult();
            await _raceRelease.Task;
            throw new RespireServerException("NOPERM release denied", "EVAL");
        }

        public ValueTask<RespireLockToken?> GetOwnerTokenAsync(
            RespireKey key,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
