using System.Text;
using Microsoft.Extensions.Logging;
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
    public async Task StreamReadPreflightHasOneFinalOwner(
        [Matrix(0, 1, 2, 3)] int path, [Matrix(false, true)] bool prefix,
        [Matrix(false, true)] bool enabled, [Matrix(false, true)] bool crossSlot)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        var physical = prefix ? "tenant:{same}:one" : "{same}:one";
        await using var server = new FakeRespServer(8, StreamReadTests.Reply(false, (physical, ["1-0"])))
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : null,
        };
        await using var root = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = prefix ? root.WithKeyPrefix("tenant:") : root;
        (RespireKey Key, RespireStreamId After)[] streams = crossSlot
            ? [("{first}:one", "0"), ("{second}:two", "0")]
            : [("{same}:one", "0"), ("{same}:two", "0")];
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task Execute()
        {
            switch (path)
            {
                case 0: await client.Streams.ReadAsync(streams, cancellationToken: deadline.Token); break;
                case 1: await client.Streams.ReadAsync(new StreamReadOptions(), streams, deadline.Token); break;
                case 2: await client.Streams.ReadGroupAsync(streams, "group", "consumer", cancellationToken: deadline.Token); break;
                case 3:
                    await using (var reader = client.Streams.ReadAllAsync(streams, cancellationToken: deadline.Token).GetAsyncEnumerator())
                        await Assert.That(await reader.MoveNextAsync()).IsTrue();
                    break;
            }
        }
        if (crossSlot)
        {
            var error = await Assert.That(Execute).Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("CROSSSLOT");
        }
        else await Execute();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(enabled && crossSlot ? 1 : 0);
        if (enabled && crossSlot)
        {
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("CROSSSLOT");
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("XREAD", StringComparison.Ordinal)))
            .IsEqualTo(crossSlot ? 0 : 1);
    }

    [Test]
    [MatrixDataSource]
    public async Task ContinuousReadRetainsRecoveredAttemptCount(
        [Matrix(0, 1, 2, 3, 4, 5, 6)] int terminal, [Matrix(false, true)] bool lateActivation)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = lateActivation ? RespireMetricGroups.None : RespireMetricGroups.Resiliency });
        var reads = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (!command.StartsWith("XREAD ", StringComparison.Ordinal)) return null;
                var attempt = Interlocked.Increment(ref reads);
                if (attempt == 1) return "-CLUSTERDOWN retry\r\n"u8.ToArray();
                if (attempt == 2)
                {
                    if (lateActivation) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
                    return StreamReadTests.Reply(false, ("events", ["6-0"]));
                }
                return terminal switch
                {
                    1 => "-NOPERM terminal\r\n"u8.ToArray(),
                    4 => StreamReadTests.Reply(false, ("unexpected", ["7-0"])),
                    5 => StreamReadTests.Reply(false, ("events", ["6-0"])),
                    6 => StreamReadTests.Reply(false, ("events", ["invalid-id"])),
                    _ => ":9\r\n"u8.ToArray(),
                };
            },
        };
        server.SuppressReply = command =>
        {
            if (terminal != 3 || !command.StartsWith("XREAD ", StringComparison.Ordinal)
                || Volatile.Read(ref reads) != 2) return false;
            waiting.TrySetResult();
            return true;
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var capture = new Capture(throwOnMeasurement: true);
        await using var reader = client.Streams.ReadAllAsync("events", "5-0", cancellationToken: deadline.Token).GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Id).IsEqualTo((RespireStreamId)"6-0");
        if (terminal != 0)
        {
            var next = reader.MoveNextAsync().AsTask();
            if (terminal == 3)
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await deadline.CancelAsync();
            }
            var error = await Assert.That(async () => await next).Throws<Exception>();
            if (terminal == 3)
                await Assert.That(((OperationCanceledException)error!).CancellationToken).IsEqualTo(deadline.Token);
        }
        var items = capture.Items.ToArray();
        var handled = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(handled.Length).IsEqualTo(lateActivation ? 0 : 1);
        if (!lateActivation)
        {
            await Assert.That(handled[0].Tags["db.response.status_code"]).IsEqualTo("CLUSTERDOWN");
            await Assert.That(handled[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(final.Length).IsEqualTo(terminal == 0 ? 0 : 1);
        if (terminal != 0)
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command.EndsWith("events 5-0", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ContinuousReadReportsFailureDuringRetryDelay(bool loggerFails)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var logger = new StreamRetryLogger(deadline, loggerFails);
        await using var server = new FakeRespServer("-CLUSTERDOWN retry\r\n"u8.ToArray());
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)], LoggerFactory = logger,
        });
        using var capture = new Capture(throwOnMeasurement: true);
        await using var reader = client.Streams.ReadAllAsync("events", cancellationToken: deadline.Token).GetAsyncEnumerator();
        var error = await Assert.That(async () => await reader.MoveNextAsync()).Throws<Exception>();
        if (loggerFails) await Assert.That(ReferenceEquals(error, logger.Failure)).IsTrue();
        else await Assert.That(((OperationCanceledException)error!).CancellationToken).IsEqualTo(deadline.Token);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("CLUSTERDOWN");
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("XREAD ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    private sealed class StreamRetryLogger(CancellationTokenSource cancellation, bool fails) : ILoggerFactory, ILogger
    {
        internal Exception Failure { get; } = new InvalidOperationException("Retry logger failed.");
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> tags
                || !Equals(tags.Last().Value, "Stream read failed; retrying after {DelayMilliseconds} ms.")) return;
            if (fails) throw Failure;
            cancellation.Cancel();
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task OrdinarySubscriptionRecoveryOwnsRejectedReplay(
        [Matrix(false, true)] bool pattern, [Matrix(false, true)] bool configured,
        [Matrix(false, true)] bool shutdown)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var subscribe = pattern ? "PSUBSCRIBE one" : "SUBSCRIBE one";
        var verb = pattern ? "psubscribe" : "subscribe";
        var acknowledgement = Encoding.ASCII.GetBytes($"*3\r\n${verb.Length}\r\n{verb}\r\n$3\r\none\r\n:1\r\n");
        var subscribed = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == subscribe
                ? Interlocked.Increment(ref subscribed) == 1 ? acknowledgement : "-NOPERM recovery rejected\r\n"u8.ToArray()
                : null,
        };
        server.SuppressReply = command =>
        {
            if (!shutdown || command != subscribe || Volatile.Read(ref subscribed) != 1) return false;
            waiting.TrySetResult();
            return true;
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
            ReconnectPolicy = configured ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 } : null,
        });
        await using var subscription = await (pattern ? client.SubscribePatternAsync("one") : client.SubscribeAsync("one"));
        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (Equals(item.Tags.GetValueOrDefault("db.response.status_code"), "NOPERM")) replayed.TrySetResult();
        });
        var index = server.ReceivedCommands.ToList().IndexOf(subscribe);
        server.CloseConnection(server.ReceivedConnectionIds[index]);
        if (shutdown) await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        else if (configured)
            await Assert.That(await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        else await replayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.DisposeAsync();
        var rejections = capture.Items.Where(item => Equals(item.Tags.GetValueOrDefault("db.response.status_code"), "NOPERM")).ToArray();
        await Assert.That(rejections.Length).IsEqualTo(shutdown ? 0 : 1);
        foreach (var item in rejections)
        {
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(capture.Items.Any(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsFalse();
        await Assert.That(capture.Items.Any(item => Equals(item.Tags["error.type"], typeof(OperationCanceledException).FullName))).IsFalse();
    }
}
