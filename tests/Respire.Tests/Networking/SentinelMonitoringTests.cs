using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelMonitoringTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ValidationCoversOnlySubscriptionsPresentBeforeDiscovery()
    {
        using var lifetime = new CancellationTokenSource();
        var gaps = new List<(RespireEndpoint, bool)>();
        var monitor = Create(lifetime, (endpoint, initial) => gaps.Add((endpoint, initial)));
        var first = new RespireEndpoint("first", 26379);
        var second = new RespireEndpoint("second", 26379);
        monitor.SubscriptionEstablished(first, true);
        var lookup = monitor.SubscriptionVersion;
        monitor.SubscriptionEstablished(second, true);
        monitor.Validated(first, lookup);
        await Assert.That(monitor.NeedsStartupValidation(first, lookup)).IsFalse();
        await Assert.That(monitor.NeedsStartupValidation(second, monitor.SubscriptionVersion)).IsTrue();
        // A newer lookup of the first reporter cannot cover the second reporter's view.
        monitor.Validated(first, monitor.SubscriptionVersion);
        await Assert.That(monitor.NeedsStartupValidation(second, monitor.SubscriptionVersion)).IsTrue();
        monitor.Validated(second, monitor.SubscriptionVersion);
        monitor.Validated(second, lookup); // A late older completion cannot reopen a covered gap.
        await Assert.That(monitor.NeedsStartupValidation(second, monitor.SubscriptionVersion)).IsFalse();
        monitor.SubscriptionEstablished(new("first", 26379), false);
        await Assert.That(monitor.SubscriptionVersion).IsEqualTo(2L);
        await Assert.That(gaps.Count).IsEqualTo(3);
        await Assert.That(gaps[2].Item2).IsFalse();
        monitor.Stop();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IndependentGapCannotBeSkippedAfterStartupValidation(bool reverse)
    {
        var startup = SentinelHint.FromGap(new("first", 26379)) with { StartupSubscriptionVersion = 2 };
        var independent = SentinelHint.FromGap(new("second", 26379));
        var mixed = reverse ? SentinelNotificationCoalescer.Merge(independent, startup)
            : SentinelNotificationCoalescer.Merge(startup, independent);
        await Assert.That(mixed.StartupSubscriptionVersion).IsEqualTo(0L);
        await Assert.That(mixed.MustRediscover).IsTrue();
        var otherStartup = independent with { StartupSubscriptionVersion = 3 };
        var merged = SentinelNotificationCoalescer.Merge(startup, otherStartup);
        await Assert.That(merged.StartupSubscriptionVersion).IsEqualTo(3L);
    }

    [Test]
    public async Task StoppedMonitorRejectsLateSubscriptionAndPublication()
    {
        using var lifetime = new CancellationTokenSource();
        var gaps = 0;
        var monitor = Create(lifetime, (_, _) => gaps++);
        var signal = monitor.CurrentMonitorRearm();
        monitor.Stop();
        monitor.SubscriptionEstablished(new("late", 26379), true);
        monitor.Published();
        await Assert.That(gaps).IsEqualTo(0);
        await Assert.That(monitor.SubscribedCount).IsEqualTo(0);
        await Assert.That(signal.IsCompleted).IsFalse();
        await Assert.That(monitor.Stop().All(task => task.IsCompleted)).IsTrue();
    }

    [Test]
    public async Task ComponentOwnsStartupParsingReconnectAndShutdown()
    {
        await using var sentinel = new FakeRespServer(16, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SUBSCRIBE ")
                ? Encoding.ASCII.GetBytes(string.Concat(command.Split(' ').Skip(1).Select((channel, index) =>
                    $"*3\r\n$9\r\nsubscribe\r\n${channel.Length}\r\n{channel}\r\n:{index + 1}\r\n")))
                : FakeRespServer.OkReply,
        };
        using var lifetime = new CancellationTokenSource();
        var endpoint = new RespireEndpoint("127.0.0.1", sentinel.Port);
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<SentinelEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var options = new RespireOptions
        {
            SentinelPrimaryName = "service", ConnectTimeout = Limit,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 },
        };
        var monitor = new SentinelMonitoring(options, null, new object(), new([endpoint]), lifetime,
            (_, parsed, _, _) => { Interlocked.Increment(ref count); received.TrySetResult(parsed); return ValueTask.CompletedTask; },
            (_, initial) => { if (initial) startup.TrySetResult(); else gap.TrySetResult(); });
        try
        {
            monitor.Published();
            await startup.Task.WaitAsync(Limit);
            await Send("other 127.0.0.1 6379 127.0.0.1 6380");
            await Send("service 127.0.0.1 6379 127.0.0.1 6380");
            var parsed = await received.Task.WaitAsync(Limit);
            await Assert.That(parsed.NewPrimary).IsEqualTo(new RespireEndpoint("127.0.0.1", 6380));
            await Assert.That(Volatile.Read(ref count)).IsEqualTo(1);
            var publication = monitor.CurrentMonitorRearm();
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(monitor.Published)));
            await publication.WaitAsync(Limit);
            sentinel.CloseConnections();
            await gap.Task.WaitAsync(Limit);
            await Assert.That(monitor.SubscribedCount).IsEqualTo(1);
        }
        finally
        {
            var tasks = monitor.Stop();
            lifetime.Cancel();
            await Task.WhenAll(tasks).WaitAsync(Limit);
        }

        Task Send(string text)
        {
            var index = sentinel.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SUBSCRIBE "));
            return sentinel.SendRawAsync(Encoding.ASCII.GetBytes(
                $"*3\r\n$7\r\nmessage\r\n$14\r\n+switch-master\r\n${text.Length}\r\n{text}\r\n"),
                sentinel.ReceivedConnectionIds[index]);
        }
    }

    private static SentinelMonitoring Create(CancellationTokenSource lifetime, Action<RespireEndpoint, bool> gap)
        => new(new() { SentinelPrimaryName = "service" }, null, new object(), new([]), lifetime,
            (_, _, _, _) => ValueTask.CompletedTask, gap);
}
