using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelReconnectPolicyTests
{
    private static readonly byte[] EmptyPeers = "*0\r\n"u8.ToArray();
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FallbackLimitSpansConfiguredAndLearnedPeers(bool learned)
    {
        await using var primary = new FakeRespServer(PrimaryRole);
        await using var last = new FakeRespServer(PrimaryReply(primary.Port), EmptyPeers);
        await using var second = new FakeRespServer("-NOPERM second unavailable\r\n"u8.ToArray(), EmptyPeers);
        await using var first = new FakeRespServer("-ERR first unavailable\r\n"u8.ToArray(), learned ? PeersReply(last.Port) : EmptyPeers);
        using var metrics = new DiscoveryMetrics(second.Port);
        var options = Options(first.Port, second.Port) with
        {
            Endpoints = learned ? [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)]
                : [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port), new("127.0.0.1", last.Port)],
        };
        var error = await Assert.That(async () => await RespireClient.ConnectAsync(options))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.InnerException is RespireServerException { Code: "NOPERM" }).IsTrue();
        await Assert.That(first.CommandsSeen).IsEqualTo(2);
        await Assert.That(second.CommandsSeen).IsEqualTo(2);
        await Assert.That(last.CommandsSeen).IsEqualTo(0);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        await Assert.That(metrics.Attempts.ToArray()).IsEquivalentTo(new long[] { 1 });
        await Assert.That(metrics.Exhaustions).IsEqualTo(1L);
    }

    [Test]
    public async Task FailedPrimaryValidationConsumesTheSameFallbackBudget()
    {
        await using var stale = new FakeRespServer("*1\r\n$5\r\nslave\r\n"u8.ToArray());
        await using var primary = new FakeRespServer(PrimaryRole);
        await using var last = new FakeRespServer(PrimaryReply(primary.Port));
        await using var second = new FakeRespServer(PrimaryReply(stale.Port), PeersReply(last.Port));
        await using var first = new FakeRespServer("$-1\r\n"u8.ToArray(), EmptyPeers);
        var error = await Assert.That(async () => await RespireClient.ConnectAsync(Options(first.Port, second.Port)))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.InnerException!.Message).Contains("valid primary ROLE");
        await stale.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(last.CommandsSeen).IsEqualTo(0);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DelayIsObservedAndCallerCancellationStopsFallback(bool cancel)
    {
        await using var second = new FakeRespServer(PrimaryReply(6379), EmptyPeers);
        await using var first = new FakeRespServer("-ERR unavailable\r\n"u8.ToArray(), EmptyPeers);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        long scheduledAt = 0;
        using var metrics = new DiscoveryMetrics(second.Port, () =>
        {
            scheduledAt = Stopwatch.GetTimestamp();
            if (cancel) cancellation.Cancel();
        });
        var options = Options(first.Port, second.Port) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(200), JitterRatio = 0, MaxAttempts = 1 },
        };
        var pending = SentinelResolver.ResolveAndConnectPrimaryAsync(options,
            static (resolved, _) => ValueTask.FromResult(resolved.PrimaryEndpoint), cancellation.Token).AsTask();
        if (cancel)
        {
            var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            await Assert.That(second.CommandsSeen).IsEqualTo(0);
        }
        else
        {
            await Assert.That((await pending).Port).IsEqualTo(6379);
            await Assert.That(Stopwatch.GetElapsedTime(scheduledAt) >= TimeSpan.FromMilliseconds(180)).IsTrue();
        }
        await Assert.That(metrics.Delays.ToArray()).IsEquivalentTo(new[] { 0.2 });
        await Assert.That(metrics.Exhaustions).IsEqualTo(0L);
    }

    [Test]
    public async Task NewResolutionStartsFreshAndSuccessDoesNotReportExhaustion()
    {
        await using var second = new FakeRespServer(2, PrimaryReply(6379));
        await using var first = new FakeRespServer(2, "-ERR unavailable\r\n"u8.ToArray());
        using var metrics = new DiscoveryMetrics(second.Port);
        for (var index = 0; index < 2; index++)
        {
            var endpoint = await SentinelResolver.ResolveAndConnectPrimaryAsync(Options(first.Port, second.Port),
                static (resolved, _) => ValueTask.FromResult(resolved.PrimaryEndpoint), default);
            await Assert.That(endpoint.Port).IsEqualTo(6379);
        }
        await Assert.That(metrics.Attempts.ToArray()).IsEquivalentTo(new long[] { 1, 1 }, CollectionOrdering.Matching);
        await Assert.That(metrics.Exhaustions).IsEqualTo(0L);
    }

    [Test]
    public async Task SecondFallbackUsesSharedExponentialAttemptNumber()
    {
        await using var third = new FakeRespServer(PrimaryReply(6379), EmptyPeers);
        await using var second = new FakeRespServer("-ERR unsupported\r\n"u8.ToArray(), EmptyPeers);
        await using var first = new FakeRespServer("$-1\r\n"u8.ToArray(), EmptyPeers);
        using var firstAttempt = new DiscoveryMetrics(second.Port);
        using var secondAttempt = new DiscoveryMetrics(third.Port);
        var options = Options(first.Port, second.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port), new("127.0.0.1", third.Port)],
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(20), JitterRatio = 0, MaxAttempts = 2 },
        };
        var endpoint = await SentinelResolver.ResolveAndConnectPrimaryAsync(options,
            static (resolved, _) => ValueTask.FromResult(resolved.PrimaryEndpoint), default);
        await Assert.That(endpoint.Port).IsEqualTo(6379);
        await Assert.That(firstAttempt.Attempts.ToArray()).IsEquivalentTo(new long[] { 1 });
        await Assert.That(firstAttempt.Delays.ToArray()).IsEquivalentTo(new[] { 0.02 });
        await Assert.That(secondAttempt.Attempts.ToArray()).IsEquivalentTo(new long[] { 2 });
        await Assert.That(secondAttempt.Delays.ToArray()).IsEquivalentTo(new[] { 0.04 });
        await Assert.That(secondAttempt.Exhaustions).IsEqualTo(0L);
    }

    [Test]
    public async Task NullPolicyPreservesAllFallbacksAndPublishesNoPolicyMetrics()
    {
        await using var primary = new FakeRespServer(PrimaryRole, FakeRespServer.PongReply);
        await using var last = new FakeRespServer(PrimaryReply(primary.Port), EmptyPeers);
        await using var second = new FakeRespServer("-ERR unsupported\r\n"u8.ToArray(), EmptyPeers);
        await using var first = new FakeRespServer("$-1\r\n"u8.ToArray(), EmptyPeers);
        using var metrics = new DiscoveryMetrics(second.Port);
        await using var client = await RespireClient.ConnectAsync(Options(first.Port, second.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port), new("127.0.0.1", last.Port)],
            ReconnectPolicy = null,
        });
        await client.PingAsync();
        await Assert.That(metrics.Attempts).IsEmpty();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(new[] { "ROLE", "PING" }, CollectionOrdering.Matching);
    }

    private static RespireOptions Options(int first, int second) => new()
    {
        Endpoints = [new("127.0.0.1", first), new("127.0.0.1", second)],
        SentinelPrimaryName = "mymaster", Connections = 1, ConnectTimeout = TimeSpan.FromSeconds(2),
        ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 },
    };

    private static byte[] PrimaryReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");
    private static byte[] PeersReply(int port)
        => Encoding.ASCII.GetBytes($"*1\r\n*4\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n${port.ToString().Length}\r\n{port}\r\n");

    private sealed class DiscoveryMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _exhaustions;
        internal ConcurrentQueue<long> Attempts { get; } = new();
        internal ConcurrentQueue<double> Delays { get; } = new();
        internal long Exhaustions => Interlocked.Read(ref _exhaustions);
        internal DiscoveryMetrics(int port, Action? scheduled = null)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name.StartsWith("respire.connection.reconnect."))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (!Matches(tags, port)) return;
                if (instrument.Name.EndsWith(".attempt")) Attempts.Enqueue(value);
                if (instrument.Name.EndsWith(".exhausted")) Interlocked.Add(ref _exhaustions, value);
            });
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                if (!Matches(tags, port)) return;
                Delays.Enqueue(value);
                scheduled?.Invoke();
            });
            _listener.Start();
        }
        private static bool Matches(ReadOnlySpan<KeyValuePair<string, object?>> tags, int port)
        {
            var endpoint = false;
            var scope = false;
            foreach (var tag in tags)
            {
                endpoint |= tag.Key == "server.port" && tag.Value is int value && value == port;
                scope |= tag.Key == "respire.reconnect.scope" && Equals(tag.Value, "sentinel-discovery");
            }
            return endpoint && scope;
        }
        public void Dispose() => _listener.Dispose();
    }
}
