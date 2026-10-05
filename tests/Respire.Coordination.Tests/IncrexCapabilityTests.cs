using System.Diagnostics;
using System.Reflection;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class IncrexCapabilityTests
{
    [Test]
    public async Task UnsupportedCacheSkipsProbeUntilDeadlineThenRetriesForUpgrade()
    {
        var calls = 0;
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return FakeRespServer.OkReply;
                // First reply reports the script's successful fallback after unknown INCREX.
                return Interlocked.Increment(ref calls) == 1
                    ? "*4\r\n:1\r\n:0\r\n:9\r\n:1\r\n"u8.ToArray()
                    : "*4\r\n:1\r\n:0\r\n:9\r\n:0\r\n"u8.ToArray();
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var coordination = new RespireCoordination(client);
        await using var first = coordination.RateLimiters.FixedWindow("first", 10, TimeSpan.FromSeconds(10));
        await using var partition = coordination.RateLimiters.FixedWindow("partition", 10, TimeSpan.FromSeconds(10));
        using var initial = await first.AcquireAsync(1);
        await Assert.That(initial.IsAcquired).IsTrue();
        await Assert.That(coordination.IncrexUnsupported).IsTrue();
        using var cached = await partition.AcquireAsync(1);
        await Assert.That(cached.IsAcquired).IsTrue();
        // Expire the existing cache without a five-minute wall-clock wait or another capability cache.
        typeof(RespireCoordination).GetField("_increxUnsupportedUntil", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordination, Stopwatch.GetTimestamp() - 1);
        await Assert.That(coordination.IncrexUnsupported).IsFalse();
        using var upgraded = await partition.AcquireAsync(1);
        await Assert.That(upgraded.IsAcquired).IsTrue();
        var scripts = server.ReceivedCommands.Where(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)).ToArray();
        await Assert.That(scripts.Length).IsEqualTo(3);
        await Assert.That(scripts[0].EndsWith(" 1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(scripts[1].EndsWith(" 0", StringComparison.Ordinal)).IsTrue();
        await Assert.That(scripts[2].EndsWith(" 1", StringComparison.Ordinal)).IsTrue();
    }
}
