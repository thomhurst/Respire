using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class RedlockWireTests
{
    private static readonly byte[] Ok = "+OK\r\n"u8.ToArray();
    private static readonly byte[] Nil = "$-1\r\n"u8.ToArray();
    private static readonly byte[] One = ":1\r\n"u8.ToArray();

    [Test]
    [Arguments(10_000, 0, 0.01, 9_898)]
    [Arguments(10_000, 1_000, 0.0, 8_998)]
    [Arguments(10, 8, 0.0, 0)]
    [Arguments(10, 9, 0.0, 0)]
    [Arguments(1_000, 0, 0.999, 0)]
    public async Task ValiditySubtractsElapsedTimeAndDrift(int durationMs, int elapsedMs, double drift, int expectedMs)
    {
        var validity = RespireRedlockNodes.CalculateValidity(
            TimeSpan.FromMilliseconds(durationMs), TimeSpan.FromMilliseconds(elapsedMs), drift);
        await Assert.That(validity).IsEqualTo(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Test]
    public async Task ValidityDoesNotOverflowForTheLongestDuration()
    {
        var validity = RespireRedlockNodes.CalculateValidity(TimeSpan.MaxValue, TimeSpan.Zero, 0.5);
        await Assert.That(validity > TimeSpan.Zero && validity < TimeSpan.MaxValue).IsTrue();
    }

    [Test]
    public async Task ValidityAndRemainingEstimateFollowTheLeaseClock()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        var clock = new ManualClock();
        var group = new RespireRedlockGroup(nodes.Clients, timeProvider: clock);
        await using var attempt = await group.TryAcquireAsync("redlock:clock", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();
        var lease = attempt.Lock;
        await Assert.That(lease.Validity).IsEqualTo(TimeSpan.FromMilliseconds(9_898));
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.FromMilliseconds(9_898));

        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(20))).IsTrue();
        await Assert.That(lease.Duration).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(lease.Validity).IsEqualTo(TimeSpan.FromMilliseconds(19_798));
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.FromMilliseconds(19_798));

        clock.Advance(TimeSpan.FromMilliseconds(19_798));
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        await Assert.That(lease.IsReleased).IsTrue();
        var commandsBefore = nodes.CommandsSeen;
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(20))).IsFalse();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(commandsBefore);
    }

    [Test]
    public async Task RenewalWithoutQuorumEndsTheLeaseAndReleasesEveryNode()
    {
        await using var nodes = await Nodes.StartAsync(static (node, command) =>
            node < 2 && command.StartsWith("SET ", StringComparison.Ordinal) && command.Contains(" IFEQ ", StringComparison.Ordinal)
                ? Nil
                : null);
        var group = new RespireRedlockGroup(nodes.Clients);
        await using var attempt = await group.TryAcquireAsync("redlock:renew", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();

        await Assert.That(await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(10))).IsFalse();
        await Assert.That(attempt.Lock.IsReleased).IsTrue();
        await Assert.That(attempt.Lock.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        foreach (var server in nodes.Servers)
            await Assert.That(server.ReceivedCommands.Count(static c => c.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsFalse();
    }

    [Test]
    public async Task ReleaseCompletesOnEveryNodeDespiteCallerCancellation()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, delayReleaseMs: 300);
        var group = new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = TimeSpan.FromSeconds(5) });
        await using var attempt = await group.TryAcquireAsync("redlock:release", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.That(await attempt.Lock.ReleaseAsync(cancellation.Token)).IsTrue();
        await Assert.That(cancellation.IsCancellationRequested).IsTrue();
        await Assert.That(attempt.Lock.IsReleased).IsTrue();
    }

    [Test]
    public async Task UnrepresentableDurationIsRejectedBeforeAnyNodeCommand()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        var group = new RespireRedlockGroup(nodes.Clients, timeProvider: new ManualClock());
        await Assert.That(async () => await group.TryAcquireAsync("redlock:overflow", TimeSpan.MaxValue))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task NodeTimeoutBeyondTheTimerRangeIsRejected()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        await Assert.That(() => new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = TimeSpan.MaxValue }))
            .Throws<ArgumentOutOfRangeException>();
        _ = new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = RespireRedlockNodes.MaxNodeTimeout });
    }

    private sealed class Nodes : IAsyncDisposable
    {
        private Nodes(FakeRespServer[] servers, RespireClient[] clients)
        {
            Servers = servers;
            Clients = clients;
        }

        public FakeRespServer[] Servers { get; }
        public RespireClient[] Clients { get; }
        public int CommandsSeen => Servers.Sum(static server => server.CommandsSeen);

        public static async Task<Nodes> StartAsync(Func<int, string, byte[]?> reply, int delayReleaseMs = 0)
        {
            var servers = new FakeRespServer[3];
            var clients = new RespireClient[3];
            for (var i = 0; i < servers.Length; i++)
            {
                var node = i;
                servers[i] = new FakeRespServer(Ok)
                {
                    ReplyOverride = (_, command) => reply(node, command)
                        ?? (command.StartsWith("DELEX ", StringComparison.Ordinal) ? One : Ok),
                };
                if (delayReleaseMs > 0) servers[i].DelayReply(1, delayReleaseMs);
                clients[i] = await FakeRespServer.ConnectClientAsync(servers[i].Port);
            }

            return new Nodes(servers, clients);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in Clients) await client.DisposeAsync();
            foreach (var server in Servers) await server.DisposeAsync();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp = TimeSpan.TicksPerSecond;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
