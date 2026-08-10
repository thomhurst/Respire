using System.Diagnostics;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class LockCommandTests
{
    [Test]
    public async Task LockCommands_WriteExpectedFramesAndParseReplies()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nowner\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.FromSeconds(30))).IsTrue();
        var queriedToken = await client.Locks.GetOwnerTokenAsync("resource");
        await Assert.That(queriedToken!.AsSpan().SequenceEqual("owner"u8)).IsTrue();
        await Assert.That(await client.Locks.ExtendAsync("resource", "owner", TimeSpan.FromSeconds(45))).IsTrue();
        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsTrue();

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SET resource owner NX PX 30000",
            "GET resource",
            $"EVALSHA {LockCommands.ExtendScript.Sha1} 1 resource owner 45000",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 resource owner",
        });
    }

    [Test]
    public async Task LockCommands_ReturnFalseWhenAcquireOrOwnershipCheckFails()
    {
        await using var server = new FakeRespServer(
            "$-1\r\n"u8.ToArray(),
            "$-1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.FromSeconds(30))).IsFalse();
        await Assert.That(await client.Locks.GetOwnerTokenAsync("resource")).IsNull();
        await Assert.That(await client.Locks.ExtendAsync("resource", "owner", TimeSpan.FromSeconds(45))).IsFalse();
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

        await Assert.That(token!.AsSpan().SequenceEqual(expectedToken)).IsTrue();
        await Assert.That(await client.Locks.ReleaseAsync("resource", token!)).IsTrue();
    }

    [Test]
    public async Task LockCommands_ApplyKeyPrefixesToSetGetAndScripts()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nowner\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");

        await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.FromSeconds(30));
        await client.Locks.GetOwnerTokenAsync("resource");
        await client.Locks.ExtendAsync("resource", "owner", TimeSpan.FromSeconds(45));
        await client.Locks.ReleaseAsync("resource", "owner");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SET tenant:resource owner NX PX 30000",
            "GET tenant:resource",
            $"EVALSHA {LockCommands.ExtendScript.Sha1} 1 tenant:resource owner 45000",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 tenant:resource owner",
        });
    }

    [Test]
    public async Task LockScripts_FallBackToEvalAndSendLuaKeysAndArgs()
    {
        await using var server = new FakeRespServer(
            "-NOSCRIPT No matching script. Please use EVAL.\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsTrue();

        var commands = server.ReceivedCommands;
        await Assert.That(commands[0]).IsEqualTo($"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 resource owner");
        await Assert.That(commands[1]).StartsWith("EVAL ");
        await Assert.That(commands[1]).Contains("redis.call('GET', KEYS[1]) == ARGV[1]");
        await Assert.That(commands[1]).Contains("redis.call('DEL', KEYS[1])");
        await Assert.That(commands[1]).EndsWith(" 1 resource owner");
    }

    [Test]
    public async Task LockCommands_ValidateTokenAndExpiryBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Locks.TryTakeAsync("resource", RespireValue.Null, TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.TryTakeAsync("resource", "", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Locks.TryTakeAsync("resource", "owner", TimeSpan.FromTicks(1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Locks.ReleaseAsync("resource", RespireValue.Null))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.ExtendAsync("resource", "", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.ExtendAsync("resource", "owner", TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task RespireLock_AcquireGeneratesTokenAndDisposeCompareAndDeletes()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var attempt = await client.Locks.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        var mutex = attempt.Lock;

        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(mutex.Key.ToString()).IsEqualTo("resource");
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(mutex.RemainingEstimate).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(mutex.RemainingEstimate).IsLessThanOrEqualTo(mutex.Duration);
        await Assert.That(mutex.ExpiresAtEstimate).IsGreaterThan(DateTimeOffset.UtcNow);
        await Assert.That(mutex.IsReleased).IsFalse();
        var token = Encoding.UTF8.GetString(mutex.Token.Span);
        await Assert.That(token.Length).IsEqualTo(32);

        await mutex.DisposeAsync();

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            $"SET resource {token} NX PX 30000",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 resource {token}",
        });
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

        await Assert.That(first.Token.Span.SequenceEqual(second.Token.Span)).IsFalse();
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

        var token = Encoding.UTF8.GetString(mutex.Token.Span);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            $"SET resource {token} NX PX 30000",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 resource {token}",
        });
    }

    [Test]
    public async Task RespireLock_ReleaseAndExtendStopAtTheHandleOnceReleased()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.AlreadyReleased);
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(mutex.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(60))).IsFalse();
        await mutex.DisposeAsync();

        // SET plus one compare-and-DEL: the repeat release, the extend, and dispose never reach the wire.
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RespireLock_ConcurrentReleasesShareTheInFlightResult()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        server.DelayReply(1, 1000);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        var first = mutex.ReleaseAsync().AsTask();
        await WaitForCommandsAsync(server, 2);
        var second = mutex.ReleaseAsync().AsTask();

        await Assert.That(await first).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(await second).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RespireLock_CancelledReleaseConservativelyStopsProtectedWork()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        server.DelayReply(1, 500);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        await using var keepAlive = await mutex.KeepAliveAsync();
        using var cancellation = new CancellationTokenSource();

        var release = mutex.ReleaseAsync(cancellation.Token).AsTask();
        await WaitForCommandsAsync(server, 2);
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
            "-NOPERM release denied\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(async () => await mutex.ReleaseAsync()).Throws<RespireServerException>();
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(async () => await mutex.ReleaseAsync()).Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(3);
    }

    [Test]
    public async Task RespireLock_ExtendRecordsDurationAndStopsAfterOwnershipLoss()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = null,
        });

        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(45))).IsTrue();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromSeconds(45));

        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(90))).IsFalse();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromSeconds(45));
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(90))).IsFalse();
        await Assert.That(server.ReceivedCommands).Count().IsEqualTo(5);
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("CLIENT ID");
    }

    [Test]
    public async Task RespireLock_NoTimeoutExtensionConnectionLossMarksOwnershipLost()
    {
        await using var server = new FakeRespServer(
            3,
            FakeRespServer.OkReply,
            ":41\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.CloseConnectionAfterCommand = 4;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = null,
        });
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(async () => await mutex.ExtendAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();

        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(server.ReceivedCommands).Contains("CLIENT ID");
        await Assert.That(server.ReceivedCommands.Any(
            command => command.StartsWith("CLIENT KILL ID 41", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RespireLock_ConcurrentExtensionsPublishMetadataInRequestOrder()
    {
        var commands = new CoordinatedLockCommands();
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner"u8.ToArray(),
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());

        var first = mutex.ExtendAsync(TimeSpan.FromSeconds(45)).AsTask();
        await commands.FirstExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = mutex.ExtendAsync(TimeSpan.FromSeconds(90)).AsTask();

        await Assert.That(commands.ExtensionCount).IsEqualTo(1);
        commands.CompleteFirstExtension();

        await Assert.That(await first).IsTrue();
        await Assert.That(await second).IsTrue();
        await Assert.That(commands.Expiries).IsEquivalentTo(
            [TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)]);
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromSeconds(90));
    }

    [Test]
    public async Task RespireLock_CancelledExtensionIsFencedAndMarksTheHandleNotOwned()
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
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        });
        await client.EnsureReliableCorrectionOrderingAsync();
        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () =>
                await mutex.ExtendAsync(TimeSpan.FromSeconds(60), cancellation.Token))
            .Throws<OperationCanceledException>();
        await WaitForCommandsAsync(server, 6);
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(10))).IsFalse();

        var extensionIndexes = server.ReceivedCommands
            .Select((command, index) => (command, index))
            .Where(item => item.command.StartsWith("EVALSHA ", StringComparison.Ordinal))
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
            "owner"u8.ToArray(),
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());

        var extension = mutex.ExtendAsync(TimeSpan.FromMinutes(5)).AsTask();
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
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":0\r\n"u8.ToArray());
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
    public async Task RespireLock_ManualShorteningReschedulesKeepAlive()
    {
        var commands = new CoordinatedLockCommands(blockFirstExtension: false);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner"u8.ToArray(),
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());

        await using var keepAlive = await mutex.KeepAliveAsync();
        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromMilliseconds(200))).IsTrue();

        await commands.SecondExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(commands.Expiries).IsEquivalentTo(
            [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)]);
    }

    [Test]
    public async Task RespireLock_KeepAliveCancelsAtLeaseDeadlineWhileRenewalIsInFlight()
    {
        var commands = new CoordinatedLockCommands(waitForCancellation: true);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner"u8.ToArray(),
            TimeSpan.FromMilliseconds(200),
            Stopwatch.GetTimestamp());
        await using var keepAlive = await mutex.KeepAliveAsync();

        await commands.FirstExtensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, keepAlive.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(keepAlive.OwnershipLost).IsTrue();
        await Assert.That(mutex.IsReleased).IsTrue();
    }

    [Test]
    public async Task RespireLock_UncertainRenewalCancelsProtectedWorkBeforeFenceCompletes()
    {
        var commands = new CoordinatedLockCommands(reportUncertain: true);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner"u8.ToArray(),
            TimeSpan.FromMilliseconds(20),
            Stopwatch.GetTimestamp());
        var keepAlive = await mutex.KeepAliveAsync();

        await commands.FenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(keepAlive.CancellationToken.IsCancellationRequested).IsTrue();
        await Assert.That(keepAlive.OwnershipLost).IsTrue();
        await Assert.That(commands.FenceCompleted.Task.IsCompleted).IsFalse();

        commands.CompleteFence();
        await keepAlive.DisposeAsync();
        await Assert.That(keepAlive.Failure).IsTypeOf<RespireConnectionException>();
    }

    [Test]
    public async Task RespireLock_UncertainManualExtensionCancelsSleepingKeepAliveBeforeFenceCompletes()
    {
        var commands = new CoordinatedLockCommands(reportUncertain: true);
        var mutex = new RespireLock(
            commands,
            "resource",
            "owner"u8.ToArray(),
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());
        var keepAlive = await mutex.KeepAliveAsync();

        var extension = mutex.ExtendAsync(TimeSpan.FromSeconds(5)).AsTask();
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
            "owner"u8.ToArray(),
            TimeSpan.FromSeconds(30),
            Stopwatch.GetTimestamp());

        var extension = mutex.ExtendAsync(TimeSpan.FromSeconds(60)).AsTask();
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
    }

    [Test]
    public async Task RespireLock_AcquireAppliesTheKeyPrefixToBothTheSetAndTheReleaseScript()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");

        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));
        var token = Encoding.UTF8.GetString(mutex.Token.Span);
        await mutex.DisposeAsync();

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            $"SET tenant:resource {token} NX PX 30000",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 tenant:resource {token}",
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
        Encoding.ASCII.GetBytes($"$32\r\n{Encoding.ASCII.GetString(mutex.Token.Span)}\r\n")
            .CopyTo(ownerReply, 0);

        await Assert.That(await root.Locks.IsHeldByAsync(mutex)).IsTrue();
        await mutex.DisposeAsync();

        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("GET tenant:resource");
    }

    [Test]
    public async Task RespireLock_PollingAcquireRetriesUntilTheWaitBudgetIsSpent()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var attempt = await client.Locks.AcquireAsync(
            "resource",
            TimeSpan.FromSeconds(30),
            wait: TimeSpan.FromMilliseconds(120),
            retryEvery: TimeSpan.FromMilliseconds(50));

        await Assert.That(attempt.Acquired).IsFalse();
        await Assert.That(server.ReceivedCommands.Count).IsGreaterThan(1);
    }

    [Test]
    public async Task RespireLock_PollingAcquireDefaultsTheRetryInterval()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var attempt = await client.Locks.AcquireAsync(
            "resource", TimeSpan.FromSeconds(30), wait: TimeSpan.FromMilliseconds(120));

        await Assert.That(attempt.Acquired).IsFalse();
        await Assert.That(server.ReceivedCommands.Count).IsGreaterThan(1);
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

    private sealed class CoordinatedLockCommands : ILockCommands, IManagedLockCommands
    {
        private readonly bool _blockFirstExtension;
        private readonly bool _reportUncertain;
        private readonly bool _raceOwnershipLoss;
        private readonly bool _waitForCancellation;
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
            bool waitForCancellation = false)
        {
            _blockFirstExtension = blockFirstExtension;
            _reportUncertain = reportUncertain;
            _raceOwnershipLoss = raceOwnershipLoss;
            _waitForCancellation = waitForCancellation;
        }

        public TaskCompletionSource FirstExtensionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondExtensionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FenceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FenceCompleted { get; } =
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

        public void CompleteRaceExtension() => _raceExtension.TrySetResult();

        public void CompleteRaceRelease() => _raceRelease.TrySetResult();

        async ValueTask<bool> IManagedLockCommands.ExtendManagedAsync(
            RespireKey key,
            RespireValue token,
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
                return await ExtendAsync(key, token, expiry, cancellationToken);
            }

            onOutcomeUncertain?.Invoke();
            FenceStarted.TrySetResult();
            await _fence.Task;
            FenceCompleted.TrySetResult();
            throw new RespireConnectionException("renewal outcome is uncertain");
        }

        public ValueTask<bool> ExtendAsync(
            RespireKey key,
            RespireValue token,
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
            RespireValue token,
            TimeSpan expiry,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async ValueTask<bool> ReleaseAsync(
            RespireKey key,
            RespireValue token,
            CancellationToken cancellationToken = default)
        {
            if (!_raceOwnershipLoss)
            {
                throw new NotSupportedException();
            }

            RaceReleaseStarted.TrySetResult();
            await _raceRelease.Task;
            throw new RespireServerException("NOPERM release denied", "EVAL");
        }

        public ValueTask<byte[]?> GetOwnerTokenAsync(
            RespireKey key,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<bool> IsHeldByAsync(
            RespireLock mutex,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
