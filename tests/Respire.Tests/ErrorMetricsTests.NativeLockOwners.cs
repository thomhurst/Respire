using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task NativeLockValidationHasOneFinalOwner(
        [Matrix("take", "release", "reset", "acquire", "wait", "retry", "throw", "extension")] string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        Exception? failure = null;
        try
        {
            switch (route)
            {
                case "take": await client.Locks.TryTakeAsync("key", default, TimeSpan.FromSeconds(30)); break;
                case "release": await client.Locks.ReleaseAsync("key", default); break;
                case "reset": await client.Locks.ResetExpiryAsync("key", "owner", TimeSpan.Zero); break;
                case "acquire": await client.Locks.AcquireAsync("key", TimeSpan.Zero); break;
                case "wait": await client.Locks.AcquireAsync("key", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(-1)); break;
                case "retry": await client.Locks.AcquireAsync("key", TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.Zero); break;
                case "throw": await client.Locks.AcquireOrThrowAsync("key", TimeSpan.Zero); break;
                case "extension": await client.Locks.AcquireAsync("key", TimeSpan.Zero, keepAlive: true); break;
            }
        }
        catch (ArgumentException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(server.ReceivedCommands).IsEmpty();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["error.type"]).IsEqualTo(failure!.GetType().FullName);
        await Assert.That(item.Tags["redis.client.errors.internal"]).IsEqualTo(false);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    public async Task NativeLockContentionConversionHasOneFinalOwner()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () => await client.Locks.AcquireOrThrowAsync("key", TimeSpan.FromSeconds(30)))
            .Throws<RespireLockNotAcquiredException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.errors.internal"]).IsEqualTo(false);
    }

    [Test]
    public async Task NativeLockRenewalGateCancellationHasOneFinalOwner()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = NativeLockMetricServer();
        server.SuppressReply = command =>
        {
            if (!command.Contains(" IFEQ ", StringComparison.Ordinal) || !command.StartsWith("SET ", StringComparison.Ordinal)) return false;
            written.TrySetResult();
            return true;
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("key", TimeSpan.FromSeconds(30));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = mutex.ResetExpiryAsync(TimeSpan.FromSeconds(30)).AsTask();
        await written.Task.WaitAsync(deadline.Token);
        using var capture = new Capture(throwOnMeasurement: true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var second = mutex.ResetExpiryAsync(TimeSpan.FromSeconds(30), cancelled.Token).AsTask();
        await Assert.That(async () => await second).Throws<OperationCanceledException>();
        await Assert.That(second.IsCanceled).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.errors.internal"]).IsEqualTo(false);
        var index = server.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SET ", StringComparison.Ordinal) && command.Contains(" IFEQ ", StringComparison.Ordinal));
        await server.SendRawAsync(FakeRespServer.OkReply, server.ReceivedConnectionIds[index]);
        await Assert.That(await first.WaitAsync(deadline.Token)).IsTrue();
        await mutex.DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NativeLockFinalCancellationWaitsForFenceCleanup(bool fenceFails)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fenced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = NativeLockMetricServer();
        server.SuppressReply = command =>
        {
            if (command.StartsWith("DELEX ", StringComparison.Ordinal)) { written.TrySetResult(); return true; }
            if (command == "CLIENT KILL ID 41") { fenced.TrySetResult(); return true; }
            return false;
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("key", TimeSpan.FromSeconds(30));
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var release = mutex.ReleaseAsync(cancellation.Token).AsTask();
        await written.Task.WaitAsync(deadline.Token);
        cancellation.Cancel();
        await fenced.Task.WaitAsync(deadline.Token);
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(release.IsCompleted).IsFalse();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
        var index = server.ReceivedCommands.ToList().IndexOf("CLIENT KILL ID 41");
        await server.SendRawAsync(fenceFails ? "-ERR fence denied\r\n"u8.ToArray() : ":0\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
        var error = await Assert.That(async () => await release.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(release.IsCanceled).IsTrue();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(fenceFails ? 2 : 1);
        if (fenceFails) await Assert.That(items[0].Tags["redis.client.errors.internal"]).IsEqualTo(true);
        await Assert.That(items[^1].Tags["redis.client.errors.internal"]).IsEqualTo(false);
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(fenceFails ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NativeLockDisposalClassifiesOnlyItsCallerVisibleFailure(bool serverFailure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = NativeLockMetricServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("key", TimeSpan.FromSeconds(30));
        if (serverFailure) server.ReplyOverride = (_, _) => "-WRONGTYPE private-key\r\n"u8.ToArray();
        else await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        if (serverFailure) await Assert.That(async () => await mutex.DisposeAsync()).Throws<RespireServerException>();
        else await mutex.DisposeAsync();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.errors.internal"]).IsEqualTo(!serverFailure);
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    public async Task NativeLockReleaseJoinersOwnFinalFailuresAndCopyRetryCounts()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = NativeLockMetricServer();
        server.ReplyOverride = (_, command) => command.StartsWith("DELEX ", StringComparison.Ordinal) ? NativeLockCommandTests.UnknownDelex
            : command.StartsWith("DELIFEQ ", StringComparison.Ordinal) ? NativeLockCommandTests.UnknownDelifeq
            : command.StartsWith("EVALSHA ", StringComparison.Ordinal) ? "-NOSCRIPT private-script\r\n"u8.ToArray()
            : NativeLockMetricReply(command);
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("EVAL ", StringComparison.Ordinal)) return false;
            written.TrySetResult();
            return true;
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var mutex = await client.Locks.AcquireOrThrowAsync("key", TimeSpan.FromSeconds(30));
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = mutex.ReleaseAsync().AsTask();
        await written.Task.WaitAsync(deadline.Token);
        var second = mutex.ReleaseAsync().AsTask();
        await Assert.That(capture.Items.Count).IsEqualTo(3);
        var index = server.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("EVAL ", StringComparison.Ordinal));
        await server.SendRawAsync("-WRONGTYPE private-key\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
        var firstError = await Assert.That(async () => await first.WaitAsync(deadline.Token)).Throws<RespireServerException>();
        var secondError = await Assert.That(async () => await second.WaitAsync(deadline.Token)).Throws<RespireServerException>();
        await Assert.That(ReferenceEquals(firstError, secondError)).IsTrue();
        var final = capture.Items.Where(item => Equals(item.Tags["redis.client.errors.internal"], false)).ToArray();
        await Assert.That(final.Length).IsEqualTo(2);
        foreach (var item in final) await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(3);
        await Assert.That(mutex.IsReleased).IsFalse();
    }

    [Test]
    public async Task SuccessfulNativeLockCallerOwnerAddsNoAllocation()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        for (var i = 0; i < 5; i++) { MeasureNativeLockOwner(false); MeasureNativeLockOwner(true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: MeasureNativeLockOwner(false), Control: MeasureNativeLockOwner(true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureNativeLockOwner(bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var owner = DispatchResponseSource<LockReleaseOutcome>.Start();
            _ = owner.Attach(new ValueTask<LockReleaseOutcome>(LockReleaseOutcome.Released)).GetAwaiter().GetResult();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static FakeRespServer NativeLockMetricServer() => new(8, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => NativeLockMetricReply(command),
    };

    private static byte[] NativeLockMetricReply(string command)
        => command == "CLIENT ID" ? ":41\r\n"u8.ToArray()
            : command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) ? ":0\r\n"u8.ToArray()
            : command.StartsWith("DELEX ", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray()
            : FakeRespServer.OkReply;
}
