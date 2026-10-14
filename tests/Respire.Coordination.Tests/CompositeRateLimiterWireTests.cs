using System.Text;
using System.Threading.RateLimiting;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class CompositeRateLimiterWireTests
{
    [Test]
    public async Task EveryAttemptUsesOneEvalAndPreservesBinaryPrefixedKey()
    {
        await using var server = new FakeRespServer(
            "*3\r\n:1\r\n:0\r\n:2\r\n"u8.ToArray(),
            "*3\r\n:0\r\n:60000\r\n:0\r\n"u8.ToArray(),
            "*3\r\n:1\r\n:0\r\n:2\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] key = [0xff, 0, 0x42];
        RespireRateLimit[] definitions = [
            RespireRateLimit.FixedWindow(3, TimeSpan.FromMinutes(1)),
            RespireRateLimit.SlidingWindow(6, TimeSpan.FromMinutes(2), 4),
            RespireRateLimit.TokenBucket(5, 1, TimeSpan.FromSeconds(10))];
        await using var limiter = client.WithKeyPrefix("tenant:").Coordination.RateLimiters.Composite(key, definitions);
        // Both the caller-owned collection and binary key must be snapshotted.
        key[0] = 0;
        definitions[0] = RespireRateLimit.FixedWindow(99, TimeSpan.FromSeconds(1));
        using var sync = limiter.AttemptAcquire();
        using var oversized = await limiter.AcquireAsync(4);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.That(async () => await limiter.AcquireAsync(1, cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(sync.IsAcquired).IsFalse();
        await Assert.That(oversized.MetadataNames).IsEmpty();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(0);

        using var accepted = await limiter.AcquireAsync(1);
        using var denied = await limiter.AcquireAsync(3);
        using var probe = await limiter.AcquireAsync(0);
        await Assert.That(accepted.IsAcquired).IsTrue();
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(probe.IsAcquired).IsTrue();
        var commands = server.ReceivedArguments;
        await Assert.That(commands.Count).IsEqualTo(3);
        foreach (var command in commands)
        {
            await Assert.That(Encoding.UTF8.GetString(command[0])).IsEqualTo("EVAL");
            await Assert.That(Encoding.UTF8.GetString(command[2])).IsEqualTo("1");
            await Assert.That(command[3].SequenceEqual("tenant:"u8.ToArray().Concat(new byte[] { 0xff, 0, 0x42 }))).IsTrue();
            await Assert.That(Encoding.UTF8.GetString(command[5])).IsEqualTo("0,3,60000,0");
            await Assert.That(command.Length).IsEqualTo(8);
        }
    }

    [Test]
    public async Task DefinitionsAndCompositeArgumentsAreValidatedWithoutIo()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
        var factory = client.Coordination.RateLimiters;
        var valid = RespireRateLimit.FixedWindow(1, TimeSpan.FromSeconds(1));
        await Assert.That(() => factory.Composite("key", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => factory.Composite("key", [])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => factory.Composite("key", Enumerable.Repeat(valid, 33).ToArray())).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => factory.Composite("key", [null!])).Throws<ArgumentException>();
        await Assert.That(() => factory.Composite(default, [valid])).Throws<ArgumentException>();
        await Assert.That(() => factory.Composite("key", [valid], -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => factory.Composite("key", [valid], queueProcessingOrder: (QueueProcessingOrder)99))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireRateLimit.FixedWindow(0, TimeSpan.FromSeconds(1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireRateLimit.FixedWindow(1, TimeSpan.FromTicks(9999))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireRateLimit.SlidingWindow(1, TimeSpan.FromSeconds(1), 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireRateLimit.SlidingWindow(1, TimeSpan.FromSeconds(1), 1025)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireRateLimit.TokenBucket(1, 0, TimeSpan.FromSeconds(1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireRateLimit.TokenBucket(int.MaxValue, 1, TimeSpan.FromDays(1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireRateLimit.SlidingWindow(1, TimeSpan.MaxValue, 1)).Throws<ArgumentOutOfRangeException>();
    }
}
