using System.Text;
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
    public async Task RawModulePrefixRejectionKeepsFinalOwner(
        [Matrix(false, true)] bool fireAndForget, [Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = root.WithKeyPrefix("tenant:");
        using var capture = new Capture(throwOnMeasurement: true);
        var sent = server.ReceivedCommands.Count;
        var pending = fireAndForget
            ? client.ExecuteFireAndForgetAsync("JSON.UNKNOWN", ["private-key"])
            : Discard(client.ExecuteAsync("JSON.UNKNOWN", ["private-key"]));
        await Assert.That(async () => await pending).ThrowsExactly<NotSupportedException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(sent);
        await Assert.That(capture.Items.Count).IsEqualTo(enabled ? 1 : 0);
        if (enabled)
        {
            var item = capture.Items.Single();
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RawPreflightPublishesOnlyAtFinalInspection(bool interpolated)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var flags = (RespireCommandFlags)int.MaxValue;
        var pending = interpolated
            ? client.ExecuteAsync($"GET key", flags)
            : client.ExecuteAsync(RespireCommands.String.GET, ["key"], flags);
        await Assert.That(pending.IsCompleted).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
        await Assert.That(async () => { using var result = await pending; }).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task BlockingAskingFailureSharesCallerHistory(
        [Matrix("raw", "catalog", "interpolated", "list")] string route,
        [Matrix(false, true)] bool failAsking)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var target = new FakeRespServer(4, failAsking ? "*-1\r\n"u8.ToArray() : "-WRONGTYPE private-key\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ASKING"
                ? failAsking ? "-NOPERM private-identity\r\n"u8.ToArray() : FakeRespServer.OkReply : null,
        };
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(4, Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"))
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task Execute()
        {
            switch (route)
            {
                case "raw": using (await client.ExecuteAsync("BLPOP", ["key", 1], cancellationToken: deadline.Token)) break;
                case "catalog": using (await client.ExecuteAsync(RespireCommands.List.BLPOP, ["key", 1], cancellationToken: deadline.Token)) break;
                case "interpolated": using (await client.ExecuteAsync($"BLPOP key {1}", cancellationToken: deadline.Token)) break;
                default: await client.Lists.PopAsync(["key"], TimeSpan.FromSeconds(1), cancellationToken: deadline.Token); break;
            }
        }
        var error = await Assert.That(Execute).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo(failAsking ? "NOPERM" : "WRONGTYPE");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("ASK");
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingAskingKeepsNoDeadlineAndCallerCancellation(bool cancel)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(4, "*-1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ASKING" ? FakeRespServer.OkReply : null,
            SuppressReply = command =>
            {
                if (!command.StartsWith("BLPOP ", StringComparison.Ordinal)) return false;
                accepted.TrySetResult();
                return cancel;
            },
        };
        target.DelayCommand("BLPOP ", 250);
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(4, Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"))
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            CommandTimeout = TimeSpan.FromMilliseconds(100), ConnectionIdleReadTimeout = TimeSpan.FromMilliseconds(100),
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var cancellation = new CancellationTokenSource();
        var pending = client.ExecuteAsync("BLPOP", ["key", 1], cancellationToken: cancellation.Token).AsTask();
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancel)
        {
            cancellation.Cancel();
            var error = await Assert.That(async () => { using var result = await pending; }).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            await Assert.That(pending.IsCanceled).IsTrue();
        }
        else
        {
            using var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(result.IsNull).IsTrue();
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(cancel ? 2 : 1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        if (cancel)
        {
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingAskingCapacityFailureHasOneFinalOwner(bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(4, Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"))
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                "PING" => FakeRespServer.PongReply,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            MaxInflightCommands = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.That(async () =>
        {
            using var result = await client.ExecuteAsync("BLPOP", ["key", 1], cancellationToken: deadline.Token);
        }).ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).IsEqualTo(
            "A validated prefixed command needs 2 in-flight slots, but this connection allows 1.");
        await Assert.That(target.ReceivedCommands.Any(command => command == "ASKING"
            || command.StartsWith("BLPOP ", StringComparison.Ordinal))).IsFalse();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(enabled ? 2 : 0);
        if (enabled)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("ASK");
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        using var reply = await client.ExecuteAsync("PING", [], cancellationToken: deadline.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(enabled ? 2 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownPreflightCountsOneFinalError(bool invalidOptions)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var node = client.Server.OnNode(new("127.0.0.1", server.Port));
        using var capture = new Capture(throwOnMeasurement: true);
        var sent = server.ReceivedCommands.Count;
        await Assert.That(async () => await node.SendShutdownAsync(invalidOptions
            ? new() { SaveMode = (RespireShutdownSaveMode)int.MaxValue } : null)).Throws<Exception>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(sent);
    }

    private static async ValueTask Discard(ValueTask<RespireResult> pending)
    {
        using var result = await pending;
    }
}
