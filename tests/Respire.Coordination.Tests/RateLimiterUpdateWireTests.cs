using System.Text;
using System.Threading.RateLimiting;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class RateLimiterUpdateWireTests
{
    private static readonly byte[] Accepted = "*3\r\n:1\r\n:0\r\n:1\r\n"u8.ToArray();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UpdateDrainsConcurrentAttemptsEvenWhenTheirCallersCancel(bool cancel)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        await using var server = new FakeRespServer(Accepted)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return false;
                var count = Interlocked.Increment(ref acquisitions);
                if (count == 2) received.TrySetResult();
                return count <= 2;
            },
            ReplyOverride = (_, command) => command.StartsWith("EVAL ", StringComparison.Ordinal)
                ? ":1\r\n"u8.ToArray() : Accepted,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.TokenBucket("key", 10, 2, TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var first = limiter.AcquireAsync(1, cancellation.Token).AsTask();
        var second = limiter.AcquireAsync(1, cancellation.Token).AsTask();
        // Both commands must reach the server before either reply: the update gate does not serialize readers.
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancel)
        {
            await cancellation.CancelAsync();
            await Assert.That(async () => await first).Throws<OperationCanceledException>();
            await Assert.That(async () => await second).Throws<OperationCanceledException>();
            await Assert.That(limiter.IdleDuration).IsNull();
        }
        var update = factory.UpdateAsync(limiter, RespireRateLimit.TokenBucket(20, 5, TimeSpan.FromMinutes(2))).AsTask();
        var later = limiter.AcquireAsync().AsTask();
        await Assert.That(update.IsCompleted).IsFalse();
        await Assert.That(later.IsCompleted).IsFalse();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
        await server.SendRawAsync(Accepted.Concat(Accepted).ToArray());
        await update.WaitAsync(TimeSpan.FromSeconds(10));
        using var after = await later.WaitAsync(TimeSpan.FromSeconds(10));
        if (!cancel)
        {
            (await first).Dispose();
            (await second).Dispose();
        }
        var commands = server.ReceivedArguments;
        await Assert.That(commands.Count).IsEqualTo(4);
        for (var i = 0; i < 2; i++)
        {
            await Assert.That(Encoding.UTF8.GetString(commands[i][4])).IsEqualTo("10");
            await Assert.That(Encoding.UTF8.GetString(commands[i][5])).IsEqualTo("60000");
            await Assert.That(Encoding.UTF8.GetString(commands[i][6])).IsEqualTo("2");
        }
        await Assert.That(Encoding.UTF8.GetString(commands[2][0])).IsEqualTo("EVAL");
        await Assert.That(Encoding.UTF8.GetString(commands[3][4])).IsEqualTo("20");
        await Assert.That(Encoding.UTF8.GetString(commands[3][5])).IsEqualTo("120000");
        await Assert.That(Encoding.UTF8.GetString(commands[3][6])).IsEqualTo("5");
    }

    [Test]
    public async Task CanceledUpdateLeavesOldSettingsUsable()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var server = new FakeRespServer(Accepted)
        {
            SuppressReply = _ =>
            {
                if (Interlocked.Increment(ref calls) != 1) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.Gcra("key", 2, TimeSpan.FromHours(1));
        var first = limiter.AcquireAsync().AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var update = factory.UpdateAsync(limiter, RespireRateLimit.Gcra(5, TimeSpan.FromHours(2)),
            cancellationToken: cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.That(async () => await update).Throws<OperationCanceledException>();
        using var rejected = await limiter.AcquireAsync(3);
        await Assert.That(rejected.IsAcquired).IsFalse();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
        await server.SendRawAsync(Accepted);
        (await first.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        using var accepted = await limiter.AcquireAsync();
        await Assert.That(accepted.IsAcquired).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DisposalWakesAcquisitionsWaitingForUpdate(bool asyncDispose, bool migrationSubmitted)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(Accepted)
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = null,
        });
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.Gcra("key", 2, TimeSpan.FromHours(1));
        var first = migrationSubmitted ? null : limiter.AcquireAsync().AsTask();
        if (first is not null) await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var update = factory.UpdateAsync(limiter, RespireRateLimit.Gcra(3, TimeSpan.FromHours(1))).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var later = limiter.AcquireAsync().AsTask();
        await Assert.That(later.IsCompleted).IsFalse();

        if (asyncDispose) await limiter.DisposeAsync();
        else limiter.Dispose();

        try
        {
            await Assert.That(async () => await later.WaitAsync(TimeSpan.FromSeconds(10)))
                .Throws<ObjectDisposedException>();
            await Assert.That(update.IsCompleted).IsFalse();
            await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
        }
        finally
        {
            // Disposal must not abandon the registered command or let migration overtake its reply.
            await server.SendRawAsync(migrationSubmitted ? ":1\r\n"u8.ToArray() : Accepted);
            if (first is not null)
            {
                (await first.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
                await Assert.That(async () => await update.WaitAsync(TimeSpan.FromSeconds(10)))
                    .Throws<ObjectDisposedException>();
            }
            else await update.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    [Arguments("ERR composite rate-limit configuration mismatch during update")]
    [Arguments("ERR migrated GCRA debt exceeds the TimeSpan range")]
    public async Task RejectedMigrationPreservesOldSettingsAndQueue(string error)
    {
        var denied = "*3\r\n:0\r\n:3600000\r\n:0\r\n"u8.ToArray();
        var accept = false;
        await using var server = new FakeRespServer(denied)
        {
            ReplyOverride = (_, command) => command.StartsWith("EVAL ", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes($"-{error}\r\n") : Volatile.Read(ref accept) ? Accepted : denied,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.Gcra("key", 2, TimeSpan.FromHours(1), queueLimit: 3);
        var queued = limiter.AcquireAsync().AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (limiter.GetStatistics()!.CurrentQueuedCount != 1) await Task.Delay(1, deadline.Token);
        await Assert.That(async () => await factory.UpdateAsync(limiter, RespireRateLimit.Gcra(3, TimeSpan.FromHours(1))))
            .Throws<RespireServerException>();
        await Assert.That(queued.IsCompleted).IsFalse();
        await Assert.That(limiter.GetStatistics()!.CurrentQueuedCount).IsEqualTo(1);
        using var oversized = await limiter.AcquireAsync(3);
        await Assert.That(oversized.IsAcquired).IsFalse();
        Volatile.Write(ref accept, true);
        // A later valid adoption wakes the retained queue.
        await factory.UpdateAsync(limiter, RespireRateLimit.Gcra(4, TimeSpan.FromHours(1)), RespireRateLimitUpdateMode.Adopt);
        using var lease = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(lease.IsAcquired).IsTrue();
    }

    [Test]
    public async Task FailedMigrationClosesLimiterAndFailsItsQueue()
    {
        var denied = "*3\r\n:0\r\n:3600000\r\n:0\r\n"u8.ToArray();
        await using var server = new FakeRespServer(denied)
        {
            ReplyOverride = (_, command) => command.StartsWith("EVAL ", StringComparison.Ordinal)
                ? "-ERR migration failed\r\n"u8.ToArray() : denied,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.Gcra("key", 2, TimeSpan.FromHours(1), queueLimit: 3);
        var queued = limiter.AcquireAsync().AsTask();
        await Assert.That(async () => await factory.UpdateAsync(limiter, RespireRateLimit.Gcra(3, TimeSpan.FromHours(1))))
            .Throws<RespireServerException>();
        await Assert.That(async () => await queued.WaitAsync(TimeSpan.FromSeconds(10))).Throws<Exception>();
        await Assert.That(async () => await limiter.AcquireAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task InvalidUpdatesAndAdoptionDoNotAccessRedis()
    {
        await using var server = new FakeRespServer(Accepted);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.Gcra("key", 2, TimeSpan.FromHours(1));
        await Assert.That(async () => await factory.UpdateAsync(limiter, RespireRateLimit.FixedWindow(2, TimeSpan.FromSeconds(1))))
            .Throws<ArgumentException>();
        await Assert.That(async () => await factory.UpdateAsync(limiter, [RespireRateLimit.Gcra(2, TimeSpan.FromHours(1))]))
            .Throws<ArgumentException>();
        await Assert.That(async () => await factory.UpdateAsync(limiter, RespireRateLimit.Gcra(3, TimeSpan.FromHours(1)), (RespireRateLimitUpdateMode)99))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.WithKeyPrefix("other:").Coordination.RateLimiters.UpdateAsync(limiter, RespireRateLimit.Gcra(3, TimeSpan.FromHours(1))))
            .Throws<ArgumentException>();
        await using var composite = factory.Composite("composite", [RespireRateLimit.FixedWindow(2, TimeSpan.FromHours(1))]);
        await Assert.That(async () => await factory.UpdateAsync(composite, [RespireRateLimit.TokenBucket(2, 1, TimeSpan.FromHours(1))]))
            .Throws<ArgumentException>();
        await Assert.That(async () => await factory.UpdateAsync(composite, new RespireRateLimit[] { RespireRateLimit.FixedWindow(3, TimeSpan.FromHours(1)), RespireRateLimit.FixedWindow(4, TimeSpan.FromHours(1)) }))
            .Throws<ArgumentException>();
        await Assert.That(() => factory.Composite("gcra-composite", [RespireRateLimit.Gcra(2, TimeSpan.FromHours(1))]))
            .Throws<ArgumentException>();
        await factory.UpdateAsync(limiter, RespireRateLimit.Gcra(3, TimeSpan.FromHours(2)), RespireRateLimitUpdateMode.Adopt);
        await factory.UpdateAsync(limiter, RespireRateLimit.Gcra(3, TimeSpan.FromHours(2)));
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(0);
        limiter.Dispose();
        await Assert.That(async () => await factory.UpdateAsync(limiter, RespireRateLimit.Gcra(4, TimeSpan.FromHours(1))))
            .Throws<ObjectDisposedException>();
    }
}
