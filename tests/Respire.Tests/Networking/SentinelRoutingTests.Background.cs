using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class SentinelRoutingTests
{
    [Test]
    public async Task LateGenerationResponseCannotRetireDuringShutdown()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var router = client.Core.Sentinel!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new SentinelMonitorProbe { DisposeClient = () => new(release.Task) };
        router.Monitoring.ClientFactory = _ => probe.Client;
        try
        {
            await client.PingAsync();
            await SentinelTestSetup.WaitForStartupAsync(client);
            var generation = router.Current!;
            var connection = generation.Multiplexer.GetConnection();
            var disposal = client.DisposeAsync().AsTask();
            await probe.ClientCleanup.Task.WaitAsync(Limit);
            // Background shutdown is still joining the probe, so generation disposal
            // has not retired this connection yet. A late response must not do so.
            await Assert.That(disposal.IsCompleted).IsFalse();
            await Assert.That(generation.IsRetired).IsFalse();
            using var response = Respire.Protocol.RespValue.Error("READONLY late response");
            generation.ObserveResponse(connection, "SET", in response);
            await Assert.That(generation.IsRetired).IsFalse();
            await Assert.That(generation.CountedAsRetired).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            await client.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    [Test]
    public async Task LateSourceResolutionRemainsJoinedButCannotRetireAfterShutdown()
    {
        await using var primary = Primary();
        await using var promoted = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var router = client.Core.Sentinel!;
        var probe = new SentinelMonitorProbe();
        router.Monitoring.ClientFactory = _ => probe.Client;
        var release = new TaskCompletionSource<System.Net.IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var clock = new FenceClock();
        router.ShutdownClock = clock;
        Task[] background = [];
        try
        {
            await client.PingAsync();
            await SentinelTestSetup.WaitForStartupAsync(client);
            var original = router.Current!;
            router.HostResolver = (host, _) => { queries.Enqueue(host); started.TrySetResult(); return release.Task; };
            sentinel.SuppressReply = command => command.StartsWith("SENTINEL GET-MASTER");
            var text = $"mymaster old.alias {primary.Port} new.alias {primary.Port}";
            probe.Messages.Writer.TryWrite(new("+switch-master", null,
                System.Text.Encoding.ASCII.GetBytes(text), null!));
            await probe.MessageProcessed.Task.WaitAsync(Limit);
            await started.Task.WaitAsync(Limit);
            await Assert.That(router.PendingSwitchSourceResolutions).IsEqualTo(1);
            await Assert.That(original.IsRetired).IsFalse();
            var disposal = client.DisposeAsync().AsTask();
            (await ReadFenceTimerAsync(clock, TimeSpan.FromSeconds(10))).Fire();
            await disposal.WaitAsync(Limit);
            background = router.Monitoring.Stop();
            await Assert.That(background.Any(task => !task.IsCompleted)).IsTrue();
            var retired = original.IsRetired;
            release.TrySetResult([System.Net.IPAddress.Loopback]);
            await CleanupTasks.WhenAllAsync(background).WaitAsync(Limit);
            await Assert.That(router.Current).IsSameReferenceAs(original);
            await Assert.That(original.IsRetired).IsEqualTo(retired);
            await Assert.That(promoted.CommandsSeen).IsEqualTo(0);
            await Assert.That(queries.ToArray()).IsEquivalentTo(new[] { "old.alias" });
        }
        finally
        {
            release.TrySetResult([System.Net.IPAddress.Loopback]);
            await CleanupTasks.WhenAllAsync(background).WaitAsync(Limit);
        }
    }

    [Test]
    public async Task MonitorCleanupTimeoutFailureIsNotMistakenForTheShutdownDeadline()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        var client = RespireClient.Create(Options(sentinel.Port));
        var failure = new TimeoutException("monitor cleanup failed");
        var probe = new SentinelMonitorProbe { DisposeClient = () => ValueTask.FromException(failure) };
        client.Core.Sentinel!.Monitoring.ClientFactory = _ => probe.Client;
        try
        {
            await client.PingAsync();
            await SentinelTestSetup.WaitForStartupAsync(client);
            var error = await Assert.That(() => client.DisposeAsync().AsTask().WaitAsync(Limit)).ThrowsExactly<TimeoutException>();
            await Assert.That(error).IsSameReferenceAs(failure);
            await Assert.That(probe.SubscriptionCleanup.Task.IsCompleted).IsTrue();
        }
        finally { try { await client.DisposeAsync(); } catch (Exception) { } }
    }

    [Test]
    public async Task CancellationCallbacksShareTheBackgroundShutdownBound()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var router = client.Core.Sentinel!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new SentinelMonitorProbe
        {
            // Keep the registration alive until the callback really starts, even if
            // the cancelled iterator finishes first and begins subscription cleanup.
            DisposeSubscription = () => new(entered.Task),
            CancellationCallback = () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                exited.TrySetResult();
            },
        };
        router.Monitoring.ClientFactory = _ => probe.Client;
        var clock = new FenceClock();
        router.ShutdownClock = clock;
        try
        {
            await client.PingAsync();
            await SentinelTestSetup.WaitForStartupAsync(client);
            var disposal = client.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(Limit);
            await Assert.That(exited.Task.IsCompleted).IsFalse();
            (await ReadFenceTimerAsync(clock, TimeSpan.FromSeconds(10))).Fire();
            await disposal.WaitAsync(Limit);
            await Assert.That(client.IsConnected).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            await exited.Task.WaitAsync(Limit);
            await CleanupTasks.WhenAllAsync(router.Monitoring.Stop()).WaitAsync(Limit);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownStartsBothMonitorCleanupsAndBoundsUncooperativeResources(bool stalledUnsubscribe)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var router = client.Core.Sentinel!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new SentinelMonitorProbe();
        if (stalledUnsubscribe) probe.DisposeSubscription = () => new(release.Task);
        else probe.DisposeClient = () => new(release.Task);
        router.Monitoring.ClientFactory = _ => probe.Client;
        var clock = new FenceClock();
        router.ShutdownClock = clock;
        Task[] background = [];
        try
        {
            await client.PingAsync();
            await SentinelTestSetup.WaitForStartupAsync(client);
            if (stalledUnsubscribe)
            {
                probe.Messages.Writer.TryComplete();
                await probe.SubscriptionCleanup.Task.WaitAsync(Limit);
                await Assert.That(probe.ClientCleanup.Task.IsCompleted).IsFalse();
            }
            var disposal = client.DisposeAsync().AsTask();
            await probe.ClientCleanup.Task.WaitAsync(Limit);
            await probe.SubscriptionCleanup.Task.WaitAsync(Limit);
            await Assert.That(disposal.IsCompleted).IsFalse();
            var timeout = await ReadFenceTimerAsync(clock, TimeSpan.FromSeconds(10));
            timeout.Fire();
            await disposal.WaitAsync(Limit);
            await Assert.That(client.IsConnected).IsFalse();
            background = router.Monitoring.Stop();
            await Assert.That(background.Any(task => !task.IsCompleted)).IsTrue();
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(background).WaitAsync(Limit);
        }
    }

    [Test]
    public async Task ShutdownAggregatesBothMonitorCleanupFailures()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        var client = RespireClient.Create(Options(sentinel.Port));
        var first = new IOException("client cleanup");
        var second = new OutOfMemoryException("explicitly injected cleanup failure");
        var probe = new SentinelMonitorProbe
        {
            DisposeClient = () => ValueTask.FromException(first),
            DisposeSubscription = () => ValueTask.FromException(second),
        };
        client.Core.Sentinel!.Monitoring.ClientFactory = _ => probe.Client;
        try
        {
            await client.PingAsync();
            await SentinelTestSetup.WaitForStartupAsync(client);
            var error = await Assert.That(() => client.DisposeAsync().AsTask().WaitAsync(Limit))
                .Throws<Exception>();
            IReadOnlyList<Exception> errors = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [error!];
            await Assert.That(errors).Contains(first);
            await Assert.That(errors).Contains(second);
            await Assert.That(probe.SubscriptionCleanup.Task.IsCompleted).IsTrue();
            await Assert.That(client.IsConnected).IsFalse();
        }
        finally { try { await client.DisposeAsync(); } catch (Exception) { } }
    }

    [Test]
    public async Task LateMonitorMessageCannotRetireOrPublishAfterBoundedShutdown()
    {
        await using var primary = Primary();
        await using var promoted = Primary();
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var router = client.Core.Sentinel!;
        var probe = new SentinelMonitorProbe { IgnoreCancellation = true };
        router.Monitoring.ClientFactory = _ => probe.Client;
        var clock = new FenceClock();
        router.ShutdownClock = clock;
        Task[] background = [];
        try
        {
            await client.PingAsync();
            await SentinelTestSetup.WaitForStartupAsync(client);
            var original = router.Current!;
            var disposal = client.DisposeAsync().AsTask();
            await probe.Cancelled.Task.WaitAsync(Limit);
            (await ReadFenceTimerAsync(clock, TimeSpan.FromSeconds(10))).Fire();
            await disposal.WaitAsync(Limit);
            var retired = original.IsRetired;
            Volatile.Write(ref port, promoted.Port);
            var text = $"mymaster 127.0.0.1 {primary.Port} 127.0.0.1 {promoted.Port}";
            probe.Messages.Writer.TryWrite(new RespireMessage("+switch-master", null,
                System.Text.Encoding.ASCII.GetBytes(text), null!));
            background = router.Monitoring.Stop();
            probe.Messages.Writer.TryComplete();
            await Task.WhenAll(background).WaitAsync(Limit);
            await Assert.That(router.Current).IsSameReferenceAs(original);
            await Assert.That(original.IsRetired).IsEqualTo(retired);
            await Assert.That(promoted.CommandsSeen).IsEqualTo(0);
        }
        finally
        {
            probe.Messages.Writer.TryComplete();
            await Task.WhenAll(background).WaitAsync(Limit);
        }
    }
}
