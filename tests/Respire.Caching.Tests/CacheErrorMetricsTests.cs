using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

[NotInParallel]
public class CacheErrorMetricsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrderedCorrectionCopiesItsRetirementRetryIntoTheCacheOwner(bool refresh)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var getPrefix = $"EVALSHA {RespireDistributedCache.GetAndRefreshScript.Sha1} ";
            await using var server = new FakeRespServer(16, FakeRespServer.OkReply);
            server.ReplyOverride = (connection, command) => command switch
            {
                "CLUSTER SLOTS" => System.Text.Encoding.ASCII.GetBytes(
                    $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
                "CLIENT ID" => System.Text.Encoding.ASCII.GetBytes($":{1000 + connection}\r\n"),
                _ when command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) => ":0\r\n"u8.ToArray(),
                _ when command.StartsWith("EVAL ", StringComparison.Ordinal) => ":0\r\n"u8.ToArray(),
                _ => null,
            };
            server.SuppressReply = command =>
            {
                if (!command.StartsWith(getPrefix, StringComparison.Ordinal)) return false;
                reading.TrySetResult();
                return true;
            };
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
                ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
            });
            await client.EnsureReliableCorrectionOrderingAsync();
            await using var cache = new RespireDistributedCache(client);
            var recorded = new ConcurrentQueue<Dictionary<string, object?>>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    observer.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
                recorded.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            listener.Start();
            using var caller = new CancellationTokenSource();
            var pending = refresh ? cache.RefreshAsync("key", caller.Token) : cache.GetAsync("key", caller.Token);
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var index = server.ReceivedCommands.ToList().FindLastIndex(command =>
                command.StartsWith(getPrefix, StringComparison.Ordinal));
            var original = client.Core.Cluster!.GetMultiplexer(new("127.0.0.1", server.Port));
            var retirement = original.RetireAsync();
            caller.Cancel();
            // Settle the old FIFO after retirement has rejected further correction admission.
            // The correction must retry through the original-peer lease, rather than abandon it.
            await server.SendRawAsync("*2\r\n:0\r\n$5\r\nvalue\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)))
                .Throws<OperationCanceledException>();
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            var items = recorded.ToArray();
            await Assert.That(items.Length).IsEqualTo(2);
            await Assert.That((bool)items[0]["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0]["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            await Assert.That((bool)items[1]["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1]["redis.client.operation.retry_attempts"]).IsEqualTo(1);
            await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DetachedCorrectionRetainsItsLeaseAndReportsHandledFailures(bool refresh)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var correcting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var getPrefix = $"EVALSHA {RespireDistributedCache.GetAndRefreshScript.Sha1} ";
            var correctionPrefix = $"EVALSHA {RespireDistributedCache.CapRefreshedTtlScript.Sha1} ";
            await using var server = new FakeRespServer(32, FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray()
                    : command.StartsWith("EVAL ", StringComparison.Ordinal)
                        ? "-NOPERM detached correction rejected\r\n"u8.ToArray() : null,
                SuppressReply = command =>
                {
                    if (command.StartsWith(getPrefix, StringComparison.Ordinal))
                    {
                        reading.TrySetResult();
                        return true;
                    }
                    if (!command.StartsWith(correctionPrefix, StringComparison.Ordinal)) return false;
                    correcting.TrySetResult();
                    return true;
                },
            };
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, Connections = 1,
                Endpoints = [new("127.0.0.1", server.Port)], CommandTimeout = TimeSpan.FromSeconds(5),
            });
            await using var cache = new RespireDistributedCache(client)
            {
                CorrectionWaitBound = TimeSpan.FromMilliseconds(50),
            };
            var items = new ConcurrentQueue<Dictionary<string, object?>>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var item = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
                items.Enqueue(item);
                if (Equals(item.GetValueOrDefault("db.response.status_code"), "NOPERM")) completed.TrySetResult();
                throw new InvalidOperationException("Listener failure must remain isolated.");
            });
            listener.Start();
            var response = refresh ? cache.RefreshAsync("key") : cache.GetAsync("key");
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            var index = server.ReceivedCommands.ToList().FindLastIndex(command =>
                command.StartsWith(getPrefix, StringComparison.Ordinal));
            await server.SendRawAsync("*2\r\n:1\r\n$5\r\nvalue\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
            await correcting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await response.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(items).IsEmpty();

            // Rent unrelated owners after the foreground owner has returned. A detached
            // NOSCRIPT retry must retain its own live generation through the later EVAL.
            var owners = Enumerable.Range(0, 32).Select(_ => RespireTelemetry.ErrorObservation.Rent(force: true)).ToArray();
            try
            {
                foreach (var owner in owners) owner.SetAttempts(37);
                server.SuppressReply = null;
                index = server.ReceivedCommands.ToList().FindLastIndex(command =>
                    command.StartsWith(correctionPrefix, StringComparison.Ordinal));
                await server.SendRawAsync("-NOSCRIPT detached correction missing\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var recorded = items.ToArray();
                await Assert.That(recorded.Length).IsEqualTo(2);
                await Assert.That(recorded.All(item => (bool)item["redis.client.errors.internal"]!)).IsTrue();
                await Assert.That(recorded[0]["db.response.status_code"]).IsEqualTo("NOSCRIPT");
                await Assert.That(recorded[0]["redis.client.operation.retry_attempts"]).IsEqualTo(0);
                await Assert.That(recorded[1]["db.response.status_code"]).IsEqualTo("NOPERM");
                await Assert.That(recorded[1]["redis.client.operation.retry_attempts"]).IsEqualTo(1);
                await Assert.That(owners.All(owner => owner.Attempts == 37)).IsTrue();
                await Assert.That(await client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
            }
            finally { foreach (var owner in owners) owner.Dispose(); }
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SuccessfulDelayedReadReportsCorrectionFailureOnce(bool refresh, bool retry)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var getPrefix = $"EVALSHA {RespireDistributedCache.GetAndRefreshScript.Sha1} ";
            var correctionPrefix = $"EVALSHA {RespireDistributedCache.CapRefreshedTtlScript.Sha1} ";
            await using var server = new FakeRespServer(32, FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray()
                    : command.StartsWith(correctionPrefix, StringComparison.Ordinal) && retry
                        ? "-NOSCRIPT correction missing\r\n"u8.ToArray()
                    : command.StartsWith("EVAL", StringComparison.Ordinal)
                        ? "-NOPERM correction rejected\r\n"u8.ToArray() : null,
                SuppressReply = command =>
                {
                    if (!command.StartsWith(getPrefix, StringComparison.Ordinal)) return false;
                    reading.TrySetResult();
                    return true;
                },
            };
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, Connections = 1,
                Endpoints = [new("127.0.0.1", server.Port)], CommandTimeout = TimeSpan.FromSeconds(5),
            });
            await using var cache = client.AsDistributedCache();
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
                throw new InvalidOperationException("Listener failure must remain isolated.");
            });
            listener.Start();
            var response = refresh ? cache.RefreshAsync("key") : cache.GetAsync("key");
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                // Exceed the production tolerance while the successful read reply is parked.
                await Task.Delay(TimeSpan.FromMilliseconds(1100));
            }
            finally
            {
                server.SuppressReply = null;
                var index = server.ReceivedCommands.ToList().FindLastIndex(command =>
                    command.StartsWith(getPrefix, StringComparison.Ordinal));
                await server.SendRawAsync("*2\r\n:1\r\n$5\r\nvalue\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
            }
            var error = await Assert.That(async () => await response).ThrowsExactly<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("NOPERM");
            var recorded = items.ToArray();
            await Assert.That(recorded.Length).IsEqualTo(retry ? 2 : 1);
            await Assert.That((bool)recorded[^1]["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(recorded[^1]["db.response.status_code"]).IsEqualTo("NOPERM");
            await Assert.That(recorded[^1]["redis.client.operation.retry_attempts"]).IsEqualTo(retry ? 1 : 0);
            if (retry)
            {
                await Assert.That((bool)recorded[0]["redis.client.errors.internal"]!).IsTrue();
                await Assert.That(recorded[0]["db.response.status_code"]).IsEqualTo("NOSCRIPT");
                await Assert.That(recorded[0]["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            }
            await Assert.That(await client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GetWithoutCorrectionIdentityReportsOneFinalScriptError(bool refresh)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command == "CLIENT ID" ? "-NOPERM identity denied\r\n"u8.ToArray()
                    : command.StartsWith("EVAL", StringComparison.Ordinal) ? "-WRONGTYPE script rejected\r\n"u8.ToArray() : null,
            };
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, CommandTimeout = null, Endpoints = [new("127.0.0.1", server.Port)],
            });
            await using var cache = client.AsDistributedCache();
            var finals = new ConcurrentQueue<Dictionary<string, object?>>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var item = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
                if (!(bool)item["redis.client.errors.internal"]! && Equals(item.GetValueOrDefault("db.response.status_code"), "WRONGTYPE"))
                    finals.Enqueue(item);
            });
            listener.Start();
            await Assert.That(async () =>
            {
                if (refresh) await cache.RefreshAsync("key");
                else await cache.GetAsync("key");
            }).Throws<RespireServerException>();
            await Assert.That(finals.Count).IsEqualTo(1);
            await Assert.That(server.ReceivedCommands.Contains("CLIENT ID")).IsTrue();
            await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL", StringComparison.Ordinal))).IsTrue();
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GetReportsFinalFailureDuringCorrectionSetup(bool refresh)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(FakeRespServer.OkReply);
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
            });
            await using var cache = client.AsDistributedCache();
            await client.DisposeAsync();
            var finals = new ConcurrentQueue<Dictionary<string, object?>>();
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
                throw new InvalidOperationException("Listener failure must remain isolated.");
            });
            listener.Start();
            await Assert.That(async () =>
            {
                if (refresh) await cache.RefreshAsync("key");
                else await cache.GetAsync("key");
            }).Throws<ObjectDisposedException>();
            await Assert.That(finals.Count).IsEqualTo(1);
            await Assert.That(finals.Single()["error.type"]).IsEqualTo(typeof(ObjectDisposedException).FullName);
            await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL", StringComparison.Ordinal))).IsFalse();
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task GetFinalErrorWaitsForTtlCorrection(bool refresh, bool replace)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var correcting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new FakeRespServer(32, FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray()
                    : command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) ? ":0\r\n"u8.ToArray() : null,
                SuppressReply = command =>
                {
                    if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return false;
                    written.TrySetResult();
                    return true;
                },
            };
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
                CommandTimeout = TimeSpan.FromSeconds(5),
            });
            await client.EnsureReliableCorrectionOrderingAsync();
            await using var cache = new RespireDistributedCache(client)
            {
                CorrectionWaitBound = TimeSpan.FromMilliseconds(50),
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
            server.ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray() : null;
            server.SuppressReply = command =>
            {
                if (command.StartsWith("CLIENT KILL ", StringComparison.Ordinal))
                {
                    correcting.TrySetResult();
                    return true;
                }
                if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return false;
                written.TrySetResult();
                return true;
            };
            using var cancellation = new CancellationTokenSource();
            var correctionStart = server.ReceivedCommands.Count;
            var response = refresh ? cache.RefreshAsync("key", cancellation.Token) : cache.GetAsync("key", cancellation.Token);
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await correcting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var correctionIndex = server.ReceivedCommands.ToList().FindLastIndex(command =>
                command.StartsWith("CLIENT KILL ", StringComparison.Ordinal));
            var correctionConnection = server.ReceivedConnectionIds[correctionIndex];
            try
            {
                await Assert.That(finals.Count).IsEqualTo(0);
                await Assert.That(response.IsCompleted).IsFalse();
            }
            finally
            {
                server.SuppressReply = null;
                server.ReplyOverride = (_, command) => command.StartsWith("EVAL", StringComparison.Ordinal)
                    ? ":0\r\n"u8.ToArray() : command == "CLIENT ID" ? ":123\r\n"u8.ToArray() : null;
                await server.SendRawAsync(replace ? "-NOPERM correction rejected\r\n"u8.ToArray() : ":0\r\n"u8.ToArray(), correctionConnection);
            }
            if (replace) await Assert.That(async () => await response).Throws<RespireServerException>();
            else await Assert.That(async () => await response).Throws<OperationCanceledException>();
            var final = finals.Single();
            await Assert.That(final["error.type"]).IsEqualTo(replace
                ? typeof(RespireServerException).FullName : typeof(OperationCanceledException).FullName);
            await Assert.That(final["redis.client.operation.retry_attempts"]).IsEqualTo(replace ? 0 : 1);
            await Assert.That(handled.Count).IsEqualTo(replace ? 0 : 1);
            if (!replace)
            {
                var retry = handled.Single();
                await Assert.That(retry["error.type"]).IsEqualTo(typeof(RespireConnectionException).FullName);
                await Assert.That(retry["redis.client.operation.retry_attempts"]).IsEqualTo(0);
                // The cancelled read remains ahead of the first correction in the old FIFO.
                // Closing that socket faults the correction; its replacement must execute it.
                var arguments = server.ReceivedArguments;
                var correctionIndices = Enumerable.Range(correctionStart, arguments.Count - correctionStart)
                    .Where(index => arguments[index].Length > 1
                        && arguments[index][0].AsSpan().SequenceEqual("EVAL"u8)
                        && Encoding.UTF8.GetString(arguments[index][1]) == RespireDistributedCache.CapRefreshedTtlScript.Source)
                    .ToArray();
                var readIndex = server.ReceivedCommands.ToList().FindIndex(command =>
                    command.StartsWith("EVALSHA ", StringComparison.Ordinal));
                var connectionIds = server.ReceivedConnectionIds;
                await Assert.That(correctionIndices.Any(index => connectionIds[index] != connectionIds[readIndex])).IsTrue();
            }
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SetReportsOneFinalTrackedScriptError(bool cluster, bool retry)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var target = new FakeRespServer(FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) =>
                {
                    if (command == "CLIENT ID") return ":123\r\n"u8.ToArray();
                    if (command.StartsWith("CLIENT KILL ", StringComparison.Ordinal)) return ":0\r\n"u8.ToArray();
                    if (retry && command.StartsWith("EVALSHA ", StringComparison.Ordinal))
                        return "-NOSCRIPT private-cache-script\r\n"u8.ToArray();
                    if (command.StartsWith("EVAL", StringComparison.Ordinal))
                        return "-WRONGTYPE private-cache-key\r\n"u8.ToArray();
                    return null;
                },
            };
            var slot = ClusterHash.GetSlot("key");
            var topology = Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
            await using var seed = new FakeRespServer(topology);
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
                Endpoints = [new("127.0.0.1", cluster ? seed.Port : target.Port)],
            });
            await using var cache = client.AsDistributedCache();
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
                throw new InvalidOperationException("Listener failure must remain isolated.");
            });
            listener.Start();
            var error = await Assert.That(async () =>
                await cache.SetAsync("key", "private-value"u8.ToArray(), new DistributedCacheEntryOptions()))
                .Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
            var recorded = items.ToArray();
            await Assert.That(recorded.Length).IsEqualTo(retry ? 2 : 1);
            if (retry)
            {
                await Assert.That((bool)recorded[0]["redis.client.errors.internal"]!).IsTrue();
                await Assert.That(recorded[0]["db.response.status_code"]).IsEqualTo("NOSCRIPT");
                await Assert.That(recorded[0]["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            }
            await Assert.That((bool)recorded[^1]["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(recorded[^1]["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            await Assert.That(recorded[^1]["redis.client.operation.retry_attempts"]).IsEqualTo(retry ? 1 : 0);
            await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
                .IsTrue();
        }
        finally { RespireMetrics.Configure(previous); }
    }
}
