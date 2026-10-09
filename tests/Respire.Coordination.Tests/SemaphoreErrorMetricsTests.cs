using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

[NotInParallel]
public class SemaphoreErrorMetricsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ValidationIsOwnedBeforeAnyCommand(bool renewal)
    {
        using var metrics = new ErrorCollector();
        await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var semaphore = new RespireSemaphore(client, "key", 1);
        await using var acquired = renewal ? await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(30)) : default;
        metrics.Items.Clear();
        var commands = server.ReceivedCommands.Count;
        var error = await Assert.That(async () =>
        {
            if (renewal) await acquired.Permit!.ResetExpiryAsync(TimeSpan.Zero);
            else await semaphore.TryAcquireAsync(TimeSpan.Zero);
        }).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(error!.ParamName).IsEqualTo("expiry");
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(commands);
        var final = metrics.Items.Single();
        await Assert.That((bool)final["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(final["error.type"]).IsEqualTo(typeof(ArgumentOutOfRangeException).FullName);
        await Assert.That(final["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnerOnlyAcquireReportsRejectedOrderingProbeOnce(bool rejectKill)
    {
        using var metrics = new ErrorCollector();
        await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray());
        server.ReplyOverride = (_, command) => command switch
        {
            "CLIENT ID" when rejectKill => ":123\r\n"u8.ToArray(),
            "CLIENT ID" => "-NOPERM identity rejected\r\n"u8.ToArray(),
            _ when command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) => "-NOPERM kill rejected\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new RespireEndpoint("127.0.0.1", server.Port)],
            Connections = 1,
            Protocol = RespProtocol.Resp2,
            CommandTimeout = null,
        });
        metrics.Items.Clear();

        var error = await Assert.That(async () => await new RespireSemaphore(client, "key", 1)
            .TryAcquireAsync()).ThrowsExactly<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo("NOPERM");
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT ID")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("CLIENT KILL ", StringComparison.Ordinal)))
            .IsEqualTo(rejectKill ? 1 : 0);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL", StringComparison.Ordinal))).IsFalse();
        await Assert.That(metrics.Items.Count).IsEqualTo(1);
        var final = metrics.Items.Single();
        await Assert.That((bool)final["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(final["db.response.status_code"]).IsEqualTo("NOPERM");
        await Assert.That(final["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    public async Task PreCancelledAcquireRetainsCancellationToken()
    {
        using var metrics = new ErrorCollector();
        await using var server = new FakeRespServer(8, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var commands = server.ReceivedCommands.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.That(async () => await new RespireSemaphore(client, "key", 1)
            .TryAcquireAsync(cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(commands);
        await Assert.That(metrics.Items.Count).IsEqualTo(1);
        await Assert.That((bool)metrics.Items.Single()["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcquireRetainsScriptRetriesAndPublishesOnlyAfterCleanup(bool cleanupFails)
    {
        using var metrics = new ErrorCollector();
        await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var acquirePrefix = $"EVALSHA {RespireSemaphore.AcquireScript.Sha1} ";
        var releasePrefix = $"EVALSHA {RespireSemaphore.ReleaseScript.Sha1} ";
        var releasing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ReplyOverride = (_, command) => command.StartsWith(acquirePrefix, StringComparison.Ordinal)
            ? "-NOSCRIPT cache miss\r\n"u8.ToArray()
            : command.StartsWith("EVAL ", StringComparison.Ordinal) ? "-WRONGTYPE acquire rejected\r\n"u8.ToArray() : null;
        server.SuppressReply = command =>
        {
            if (!command.StartsWith(releasePrefix, StringComparison.Ordinal)) return false;
            releasing.TrySetResult(server.ReceivedConnectionIds[^1]);
            return true;
        };
        var acquisition = new RespireSemaphore(client, "key", 1).TryAcquireAsync(TimeSpan.FromSeconds(30)).AsTask();
        var connection = await releasing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.That(acquisition.IsCompleted).IsFalse();
            await Assert.That(metrics.Items.All(item => (bool)item["redis.client.errors.internal"]!)).IsTrue();
        }
        finally
        {
            server.SuppressReply = null;
            await server.SendRawAsync(cleanupFails ? "-NOPERM cleanup rejected\r\n"u8.ToArray() : ":1\r\n"u8.ToArray(), connection);
        }
        var error = await Assert.That(async () => await acquisition).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
        var items = metrics.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(cleanupFails ? 3 : 2);
        await Assert.That(items[0]["db.response.status_code"]).IsEqualTo("NOSCRIPT");
        await Assert.That(items[0]["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That((bool)items[^1]["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1]["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(items[^1]["redis.client.operation.retry_attempts"]).IsEqualTo(cleanupFails ? 2 : 1);
        if (cleanupFails)
        {
            await Assert.That(items[1]["db.response.status_code"]).IsEqualTo("NOPERM");
            await Assert.That(items[1]["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
    }

    [Test]
    public async Task CapacityMismatchHasOneMappedFinalAndNoRelease()
    {
        using var metrics = new ErrorCollector();
        await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        server.ReplyOverride = (_, command) => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
            ? "-SEMCAPACITY capacity mismatch\r\n"u8.ToArray() : null;
        var error = await Assert.That(async () => await new RespireSemaphore(client, "key", 1)
            .TryAcquireAsync(TimeSpan.FromSeconds(30))).ThrowsExactly<RespireSemaphoreCapacityMismatchException>();
        await Assert.That(error!.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(metrics.Items.Count).IsEqualTo(1);
        await Assert.That(metrics.Items.Single()["error.type"]).IsEqualTo(typeof(RespireSemaphoreCapacityMismatchException).FullName);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task AcquireFenceRetryIsHandledBeforeCancelledFinal()
    {
        using var metrics = new ErrorCollector();
        await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray());
        var fenceAttempts = 0;
        server.ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray()
            : !command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) ? null
            : command.Contains("SKIPME", StringComparison.Ordinal) ? ":0\r\n"u8.ToArray()
            : Interlocked.Increment(ref fenceAttempts) == 1 ? "-NOPERM fence rejected\r\n"u8.ToArray()
            : ":0\r\n"u8.ToArray();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquirePrefix = $"EVALSHA {RespireSemaphore.AcquireScript.Sha1} ";
        var releasePrefix = $"EVALSHA {RespireSemaphore.ReleaseScript.Sha1} ";
        server.SuppressReply = command =>
        {
            if (command.StartsWith(acquirePrefix, StringComparison.Ordinal))
            {
                written.TrySetResult();
                return true;
            }
            if (!command.StartsWith(releasePrefix, StringComparison.Ordinal)) return false;
            releasing.TrySetResult(server.ReceivedConnectionIds[^1]);
            return true;
        };
        using var cancellation = new CancellationTokenSource();
        var acquisition = new RespireSemaphore(client, "key", 1)
            .TryAcquireAsync(TimeSpan.FromSeconds(30), cancellation.Token).AsTask();
        await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var connection = await releasing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.That(acquisition.IsCompleted).IsFalse();
            await Assert.That(metrics.Items.All(item => (bool)item["redis.client.errors.internal"]!)).IsTrue();
            var fenceError = metrics.Items.Single(item => item.GetValueOrDefault("db.response.status_code") as string == "NOPERM");
            await Assert.That(fenceError["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        finally
        {
            server.SuppressReply = null;
            await server.SendRawAsync(":1\r\n"u8.ToArray(), connection);
        }
        var error = await Assert.That(async () => await acquisition).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(acquisition.IsCanceled).IsTrue();
        await Assert.That(fenceAttempts).IsEqualTo(2);
        var items = metrics.Items.ToArray();
        var final = items.Single(item => !(bool)item["redis.client.errors.internal"]!);
        await Assert.That((bool)final["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(final["error.type"]).IsEqualTo(typeof(OperationCanceledException).FullName);
        // Release can reroute while the fenced acquire socket retires. Count every
        // handled transport retry after the rejected fence under the same caller owner.
        var handled = items.Where(item => (bool)item["redis.client.errors.internal"]!).ToArray();
        for (var i = 0; i < handled.Length; i++)
            await Assert.That(handled[i]["redis.client.operation.retry_attempts"]).IsEqualTo(i);
        await Assert.That(final["redis.client.operation.retry_attempts"]).IsEqualTo(handled.Length);
        await Assert.That(ReferenceEquals(items[^1], final)).IsTrue();
    }

    [Test]
    public async Task DisposalAndDetachedReleaseFailuresRemainInternal()
    {
        using var metrics = new ErrorCollector();
        await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var acquired = await new RespireSemaphore(client, "key", 1).TryAcquireAsync(TimeSpan.FromSeconds(30));
        metrics.Items.Clear();
        var detachedFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        metrics.OnMeasurement = item =>
        {
            if (item.GetValueOrDefault("db.response.status_code") as string == "WRONGTYPE")
                detachedFailure.TrySetResult();
        };
        var releasePrefix = $"EVALSHA {RespireSemaphore.ReleaseScript.Sha1} ";
        var releases = 0;
        server.ReplyOverride = (_, command) => !command.StartsWith(releasePrefix, StringComparison.Ordinal) ? null
            : Interlocked.Increment(ref releases) == 1 ? "-NOPERM disposal rejected\r\n"u8.ToArray()
            : releases == 2 ? "-WRONGTYPE detached release rejected\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
        await acquired.Permit!.DisposeAsync();
        await detachedFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var errors = metrics.Items.ToArray();
        await Assert.That(errors.Length).IsEqualTo(2);
        await Assert.That(errors.All(item => (bool)item["redis.client.errors.internal"]!)).IsTrue();
        await Assert.That(errors.All(item => (int)item["redis.client.operation.retry_attempts"]! == 0)).IsTrue();
        server.ReplyOverride = null;
    }

    private sealed class ErrorCollector : IDisposable
    {
        private readonly RespireMetricsOptions _previous = RespireMetrics.Configuration;
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Dictionary<string, object?>> Items { get; } = new();
        internal Action<Dictionary<string, object?>>? OnMeasurement { get; set; }

        internal ErrorCollector()
        {
            RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var item = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
                Items.Enqueue(item);
                OnMeasurement?.Invoke(item);
            });
            _listener.Start();
        }

        public void Dispose()
        {
            _listener.Dispose();
            RespireMetrics.Configure(_previous);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReleaseFailureOwnsItsFinalErrorAfterCleanup(bool cleanupFails)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray());
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            await using var acquired = await new RespireSemaphore(client, "key", 1).TryAcquireAsync(TimeSpan.FromSeconds(30));
            var permit = acquired.Permit!;
            var releasePrefix = $"EVALSHA {RespireSemaphore.ReleaseScript.Sha1} ";
            var releases = 0;
            server.ReplyOverride = (_, command) => !command.StartsWith(releasePrefix, StringComparison.Ordinal) ? null
                : Interlocked.Increment(ref releases) == 1 ? "-WRONGTYPE release rejected\r\n"u8.ToArray()
                : cleanupFails ? "-NOPERM cleanup rejected\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
            var items = new ConcurrentQueue<Dictionary<string, object?>>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
                items.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            listener.Start();
            var error = await Assert.That(async () => await permit.ReleaseAsync()).ThrowsExactly<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
            var recorded = items.ToArray();
            await Assert.That(recorded.Length).IsEqualTo(cleanupFails ? 2 : 1);
            await Assert.That((bool)recorded[^1]["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(recorded[^1]["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            await Assert.That(recorded[^1]["redis.client.operation.retry_attempts"]).IsEqualTo(cleanupFails ? 1 : 0);
            if (cleanupFails)
            {
                await Assert.That((bool)recorded[0]["redis.client.errors.internal"]!).IsTrue();
                await Assert.That(recorded[0]["db.response.status_code"]).IsEqualTo("NOPERM");
                await Assert.That(recorded[0]["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            }
            await Assert.That(releases).IsEqualTo(2);
            await Assert.That(permit.IsReleased).IsTrue();
            server.ReplyOverride = null;
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    public async Task ExpiredRenewalReportsFailedSurrenderAsHandled()
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray());
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            await using var acquired = await new RespireSemaphore(client, "key", 1).TryAcquireAsync(TimeSpan.FromSeconds(30));
            var permit = acquired.Permit!;
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewalPrefix = $"EVALSHA {RespireSemaphore.RenewScript.Sha1} ";
            var releasePrefix = $"EVALSHA {RespireSemaphore.ReleaseScript.Sha1} ";
            server.ReplyOverride = (_, command) => command.StartsWith(releasePrefix, StringComparison.Ordinal)
                ? "-NOPERM surrender rejected\r\n"u8.ToArray() : null;
            server.SuppressReply = command =>
            {
                if (!command.StartsWith(renewalPrefix, StringComparison.Ordinal)) return false;
                written.TrySetResult();
                return true;
            };
            var items = new ConcurrentQueue<Dictionary<string, object?>>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
                items.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            listener.Start();
            var renewal = permit.ResetExpiryAsync(TimeSpan.FromMilliseconds(50)).AsTask();
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.Delay(TimeSpan.FromMilliseconds(100)); }
            finally
            {
                server.SuppressReply = null;
                var index = server.ReceivedCommands.ToList().FindLastIndex(command =>
                    command.StartsWith(renewalPrefix, StringComparison.Ordinal));
                await server.SendRawAsync(":1\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
            }
            await Assert.That(await renewal).IsFalse();
            await Assert.That(items.Count).IsEqualTo(1);
            await Assert.That((bool)items.Single()["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items.Single()["db.response.status_code"]).IsEqualTo("NOPERM");
            await Assert.That(items.Single()["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            await Assert.That(permit.IsReleased).IsTrue();
            server.ReplyOverride = null;
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PermitGateCancellationIsFinalAndLeavesPermitHeld(bool release)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(8, ":1\r\n"u8.ToArray());
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            await using var acquired = await new RespireSemaphore(client, "key", 1).TryAcquireAsync(TimeSpan.FromSeconds(30));
            var permit = acquired.Permit!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SuppressReply = command =>
            {
                if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return false;
                entered.TrySetResult();
                return true;
            };
            var firstRenewal = permit.ResetExpiryAsync(TimeSpan.FromSeconds(30)).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var items = new ConcurrentQueue<Dictionary<string, object?>>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                items.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
                throw new InvalidOperationException("listener failure");
            });
            listener.Start();
            using var cancellation = new CancellationTokenSource();
            try
            {
                var renewal = release ? permit.ReleaseAsync(cancellation.Token).AsTask()
                    : permit.ResetExpiryAsync(TimeSpan.FromSeconds(30), cancellation.Token).AsTask();
                cancellation.Cancel();
                var error = await Assert.That(async () => await renewal).Throws<OperationCanceledException>();
                await Assert.That(renewal.IsCanceled).IsTrue();
                await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
                await Assert.That(items.Count).IsEqualTo(1);
                await Assert.That((bool)items.Single()["redis.client.errors.internal"]!).IsFalse();
                await Assert.That(items.Single()["error.type"]).IsEqualTo(typeof(OperationCanceledException).FullName);
                await Assert.That(permit.IsReleased).IsFalse();
            }
            finally
            {
                server.SuppressReply = null;
                await server.SendRawAsync(":1\r\n"u8.ToArray(), server.ReceivedConnectionIds[^1]);
                await firstRenewal;
            }
            await Assert.That(await permit.VerifyStillHeldAsync()).IsTrue();
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RenewalFinalErrorWaitsForRequiredRelease(bool cleanupFails)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(32, ":1\r\n"u8.ToArray())
            {
                ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray()
                    : command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) ? ":0\r\n"u8.ToArray() : null,
            };
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, Connections = 1,
                Endpoints = [new("127.0.0.1", server.Port)], CommandTimeout = TimeSpan.FromSeconds(5),
            });
            await using var acquired = await new RespireSemaphore(client, "key", 1).TryAcquireAsync(TimeSpan.FromSeconds(30));
            var permit = acquired.Permit!;
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewalPrefix = $"EVALSHA {RespireSemaphore.RenewScript.Sha1} ";
            var releasePrefix = $"EVALSHA {RespireSemaphore.ReleaseScript.Sha1} ";
            server.SuppressReply = command =>
            {
                if (command.StartsWith(renewalPrefix, StringComparison.Ordinal))
                {
                    written.TrySetResult();
                    return true;
                }
                if (!command.StartsWith(releasePrefix, StringComparison.Ordinal)) return false;
                releasing.TrySetResult();
                return true;
            };
            var finals = new ConcurrentQueue<Dictionary<string, object?>>();
            var handled = new ConcurrentQueue<Dictionary<string, object?>>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var item = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
                if (!(bool)item["redis.client.errors.internal"]!) finals.Enqueue(item);
                else handled.Enqueue(item);
            });
            listener.Start();
            using var cancellation = new CancellationTokenSource();
            var renewal = permit.ResetExpiryAsync(TimeSpan.FromSeconds(30), cancellation.Token).AsTask();
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await releasing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await Assert.That(finals.Count).IsEqualTo(0);
                await Assert.That(renewal.IsCompleted).IsFalse();
            }
            finally
            {
                server.SuppressReply = null;
                var renewalIndex = server.ReceivedCommands.ToList().FindLastIndex(command =>
                    command.StartsWith(renewalPrefix, StringComparison.Ordinal));
                // The cancelled renewal retains its FIFO slot before the owner-only release.
                await server.SendRawAsync(cleanupFails
                    ? ":1\r\n-NOPERM cleanup rejected\r\n"u8.ToArray() : ":1\r\n:1\r\n"u8.ToArray(),
                    server.ReceivedConnectionIds[renewalIndex]);
            }
            var error = await Assert.That(async () => await renewal).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            var final = finals.Single();
            await Assert.That(final["error.type"]).IsEqualTo(typeof(OperationCanceledException).FullName);
            await Assert.That(final["redis.client.operation.retry_attempts"]).IsEqualTo(cleanupFails ? 1 : 0);
            await Assert.That(handled.Count).IsEqualTo(cleanupFails ? 1 : 0);
            if (cleanupFails)
            {
                await Assert.That(handled.Single()["db.response.status_code"]).IsEqualTo("NOPERM");
                await Assert.That(handled.Single()["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            }
        }
        finally { RespireMetrics.Configure(previous); }
    }
}
