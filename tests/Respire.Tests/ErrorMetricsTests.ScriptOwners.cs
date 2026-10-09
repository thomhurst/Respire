using System.Runtime.CompilerServices;
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
    public async Task ScriptFanOutPublishesOnlyAfterEveryTargetCompletes()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var secondWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new FakeRespServer("-NOPERM private-script\r\n"u8.ToArray());
        await using var second = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "SCRIPT FLUSH") return false;
                secondWritten.TrySetResult();
                return true;
            },
        };
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = client.Scripts.FlushAsync(cancellationToken: deadline.Token).AsTask();
        await secondWritten.Task.WaitAsync(deadline.Token);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
        var index = second.ReceivedCommands.ToList().IndexOf("SCRIPT FLUSH");
        await second.SendRawAsync(FakeRespServer.OkReply, second.ReceivedConnectionIds[index]);
        await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.errors.internal"]).IsEqualTo(false);
    }

    [Test]
    [MatrixDataSource]
    public async Task ScriptPreflightHasOneFinalOwner(
        [Matrix("integer", "string", "generic", "load", "exists", "flush")] string route,
        [Matrix(false, true)] bool disposed)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        if (disposed) await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        Exception? failure = null;
        try
        {
            switch (route)
            {
                case "integer": await client.Scripts.ExecuteIntegerAsync(null!); break;
                case "string": await client.Scripts.ExecuteStringAsync(null!); break;
                case "generic": await client.Scripts.ExecuteAsync<int>(null!); break;
                case "load": await client.Scripts.LoadAsync(null!); break;
                case "exists": await client.Scripts.ExistsAsync([]); break;
                case "flush": await client.Scripts.FlushAsync((ScriptFlushMode)99); break;
            }
        }
        catch (ArgumentException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["error.type"]).IsEqualTo(failure!.GetType().FullName);
        await Assert.That(item.Tags["redis.client.errors.internal"]).IsEqualTo(false);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    public async Task SuccessfulScriptCallerOwnershipAddsNoAllocation()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        for (var i = 0; i < 5; i++) { MeasureScriptCaller(false); MeasureScriptCaller(true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: MeasureScriptCaller(false), Control: MeasureScriptCaller(true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureScriptCaller(bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var owner = DispatchResponseSource<long>.Start();
            var response = owner.Attach(new ValueTask<long>(42));
            _ = response.GetAwaiter().GetResult();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
