using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CacheStampedeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ContentionDeadlineExpiresDuringCacheRead(bool published)
    {
        var waitTimeout = TimeSpan.FromSeconds(2);
        var limit = TimeSpan.FromSeconds(10);
        var retryReadReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] missing = "*4\r\n$-1\r\n:0\r\n:0\r\n$0\r\n\r\n"u8.ToArray();
        byte[] cached = "*4\r\n$5\r\nvalue\r\n:10000\r\n:1\r\n$1\r\nv\r\n"u8.ToArray();
        int acquisitions = 0;
        int factories = 0;
        await using var server = new FakeRespServer(6, FakeRespServer.OkReply);
        server.ReplyOverride = (connection, command) =>
        {
            if (command == "CLIENT ID")
            {
                return Encoding.ASCII.GetBytes($":{connection + 1}\r\n");
            }

            if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal))
            {
                return FakeRespServer.OkReply;
            }

            switch (server.ReceivedArguments[^1].Length)
            {
                case 5 when acquisitions == 0:
                    server.GateCommand(command, releaseRetryRead.Task, () => retryReadReceived.TrySetResult());
                    return missing;
                case 5:
                    return published ? cached : missing;
                case 9:
                    // The first acquisition contends. Any later acquisition would succeed.
                    return Interlocked.Increment(ref acquisitions) == 1 ? ":0\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
                default:
                    return ":1\r\n"u8.ToArray();
            }
        };
#pragma warning disable RESP010 // The test owns the client and the fake server for this scenario.
        await using RespireClient client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = limit,
            ThreadPoolMonitoring = false,
        });
#pragma warning restore RESP010
        Task<string?> pending = client.GetOrSetAsync<string>("key", new RespireCacheAsideOptions { WaitTimeout = waitTimeout },
            _ =>
            {
                Interlocked.Increment(ref factories);
                return ValueTask.FromResult<string?>(null);
            }, TimeSpan.FromMinutes(1)).AsTask();

        try
        {
            await retryReadReceived.Task.WaitAsync(limit);
            await Task.Delay(waitTimeout);
        }
        finally
        {
            releaseRetryRead.TrySetResult();
        }

        using var testTimeout = new CancellationTokenSource(limit);

        if (published)
        {
            await Assert.That(await pending.WaitAsync(testTimeout.Token)).IsEqualTo("value");
        }
        else
        {
            await Assert.That(async () => await pending.WaitAsync(testTimeout.Token)).ThrowsExactly<TimeoutException>();
        }

        await Assert.That(acquisitions).IsEqualTo(1);
        await Assert.That(factories).IsEqualTo(0);
    }

    [Test]
    public async Task ExistingDefaultTokenCallsRemainUnambiguous()
    {
        using var endpoint = new ReservedUnavailablePort();
#pragma warning disable RESP010 // This validation-only client is owned and disposed by the test and must not connect to the unavailable endpoint.
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)],
            ThreadPoolMonitoring = false,
        });
#pragma warning restore RESP010
        IRespireClient contract = client;
        Func<CancellationToken, ValueTask<string?>> factory = _ => ValueTask.FromResult<string?>("value");

        // Invalid expiry rejects before network access; these calls also guard source compatibility.
        await Assert.That(async () => await client.GetOrSetAsync("key", factory, TimeSpan.Zero, default))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await contract.GetOrSetAsync("key", factory, TimeSpan.Zero, default))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.GetOrSetAsync("key", factory, default, default))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await contract.GetOrSetAsync("key", factory, default, default))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(100L, 10d, 0d, 0.0001d, false)]
    [Arguments(100L, 10d, 1d, 0.5d, false)]
    [Arguments(100L, 10d, 1d, 0.00001d, true)]
    [Arguments(100L, 10d, 2d, 0.001d, true)]
    [Arguments(1L, 10d, 1d, 0d, true)]
    public async Task XFetchElectionUsesDurationBetaAndLogSample(long remaining, double duration, double beta,
        double sample, bool expected)
        => await Assert.That(RespireClient.ShouldRefreshCacheAside(remaining, duration, beta, sample)).IsEqualTo(expected);

    [Test]
    public async Task LockIdentityIsDeterministicBinarySafeAndPreservesClusterSlots()
    {
        for (var index = 0; index < 16_384; index++)
        {
            RespireKey key = new byte[] { 0, 255, (byte)(index >> 8), (byte)index, (byte)'{', (byte)'}' };
            var first = RespireClient.CreateCacheAsideLockKey(key);
            await Assert.That(first.ClusterSlot).IsEqualTo(key.ClusterSlot);
            await Assert.That(first.ToBytes().SequenceEqual(RespireClient.CreateCacheAsideLockKey(key).ToBytes())).IsTrue();
        }

        await Assert.That(RespireClient.CreateCacheAsideLockKey("{same}:one").ToBytes()
            .SequenceEqual(RespireClient.CreateCacheAsideLockKey("{same}:two").ToBytes())).IsFalse();
    }

    [Test]
    public async Task InvalidOptionsAreRejected()
    {
        RespireCacheAsideOptions[] invalid =
        [
            new() { EarlyRefreshBeta = -1 }, new() { EarlyRefreshBeta = double.NaN },
            new() { EarlyRefreshBeta = double.PositiveInfinity }, new() { LockTimeout = TimeSpan.Zero },
            new() { WaitTimeout = TimeSpan.FromMilliseconds(-1) }, new() { RetryDelay = TimeSpan.Zero },
            new() { LockTimeout = TimeSpan.FromDays(100) }, new() { StaleWhileRevalidate = TimeSpan.FromTicks(-1) },
            new() { DistributedSingleFlight = false, StaleWhileRevalidate = TimeSpan.FromSeconds(1) },
        ];

        foreach (var options in invalid)
        {
            await Assert.That(() => options.Validate()).Throws<ArgumentException>();
        }

        new RespireCacheAsideOptions { WaitTimeout = TimeSpan.Zero }.Validate();
    }
}
