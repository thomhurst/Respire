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
        server.DelayReply(1, 100);
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
    public async Task RespireLock_ExtendRecordsDurationAndStopsAfterOwnershipLoss()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            ":1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var mutex = await client.Locks.AcquireOrThrowAsync("resource", TimeSpan.FromSeconds(30));

        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(45))).IsTrue();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromSeconds(45));

        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(90))).IsFalse();
        await Assert.That(mutex.Duration).IsEqualTo(TimeSpan.FromSeconds(45));
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(90))).IsFalse();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(3);
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
    public async Task KeepAliveDelayAccountsForElapsedLeaseTime()
    {
        await Assert.That(RespireLockKeepAlive.GetRenewalDelay(
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(25)))
            .IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(RespireLockKeepAlive.GetRenewalDelay(
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15)))
            .IsEqualTo(TimeSpan.Zero);
        await Assert.That(RespireLockKeepAlive.GetRenewalDelay(
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)))
            .IsEqualTo(TimeSpan.Zero);
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
}
