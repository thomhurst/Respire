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
