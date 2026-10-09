using System.Reflection;
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
    [Arguments("load-source")]
    [Arguments("load-library")]
    [Arguments("delete")]
    [Arguments("flush")]
    [Arguments("restore")]
    [Arguments("execute")]
    public async Task FunctionEntryOwnsSynchronousPreflight(string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        using var capture = new Capture(throwOnMeasurement: true);
        Exception? failure = null;
        try
        {
            // None of these invalid inputs may return a response or reach connection setup.
            switch (route)
            {
                case "load-source": _ = client.Functions.LoadAsync(" "); break;
                case "load-library": _ = client.Functions.LoadAsync((RespireFunctionLibrary)null!); break;
                case "delete": _ = client.Functions.DeleteAsync(" "); break;
                case "flush": _ = client.Functions.FlushAsync((FunctionFlushMode)99); break;
                case "restore": _ = client.Functions.RestoreAsync(default, (FunctionRestorePolicy)99); break;
                default: _ = client.Functions.ExecuteAsync(null!); break;
            }
        }
        catch (Exception error) { failure = error; }
        await Assert.That(failure is ArgumentException).IsTrue();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["error.type"]).IsEqualTo(failure!.GetType().FullName);
        await Assert.That(item.Tags["redis.client.errors.internal"]).IsEqualTo(false);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [MatrixDataSource]
    public async Task FunctionFanOutPublishesOnceAfterAllTargetsFinish(
        [Matrix("load", "delete", "flush", "restore")] string route,
        [Matrix(false, true)] bool conversionFailure, [Matrix(false, true)] bool lateSelection)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = lateSelection ? RespireMetricGroups.None : RespireMetricGroups.Resiliency });
        // LOAD checks string consistency; the other mutations reject a non-OK reply.
        var reply = (conversionFailure, route) switch
        {
            (true, "load") => "$6\r\nsecond\r\n"u8.ToArray(),
            (true, _) => "*0\r\n"u8.ToArray(),
            _ => "-NOPERM function rejected\r\n"u8.ToArray(),
        };
        var firstReply = conversionFailure && route == "load" ? "$5\r\nfirst\r\n"u8.ToArray() : reply;
        await using var first = new FakeRespServer(firstReply);
        await using var second = new FakeRespServer(reply)
        {
            SuppressReply = command => command.StartsWith("FUNCTION ", StringComparison.Ordinal),
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task Execute()
        {
            switch (route)
            {
                case "load": await client.Functions.LoadAsync("source", cancellationToken: deadline.Token); break;
                case "delete": await client.Functions.DeleteAsync("sample", deadline.Token); break;
                case "flush": await client.Functions.FlushAsync(cancellationToken: deadline.Token); break;
                default: await client.Functions.RestoreAsync("binary"u8.ToArray(), cancellationToken: deadline.Token); break;
            }
        }
        using var capture = new Capture(throwOnMeasurement: true);
        var pending = Execute();
        while (!second.ReceivedCommands.Any(command => command.StartsWith("FUNCTION ", StringComparison.Ordinal)))
            await Task.Delay(1, deadline.Token);
        if (lateSelection) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(capture.Items).IsEmpty();
        await second.SendRawAsync(reply);
        Exception? failure = null;
        try { await pending.WaitAsync(deadline.Token); }
        catch (Exception error) { failure = error; }
        await Assert.That(failure is RespireException).IsTrue();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["error.type"]).IsEqualTo(failure!.GetType().FullName);
        await Assert.That(item.Tags["redis.client.errors.internal"]).IsEqualTo(false);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(first.ReceivedCommands.Count).IsEqualTo(1);
        await Assert.That(second.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("execute")]
    [Arguments("integer")]
    [Arguments("string")]
    [Arguments("generic")]
    [Arguments("load-source")]
    [Arguments("load-library")]
    [Arguments("list")]
    [Arguments("delete")]
    [Arguments("flush")]
    [Arguments("dump")]
    [Arguments("restore")]
    [Arguments("stats")]
    public async Task SuccessfulFunctionRouteRentsNoErrorObservation(string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var reply = route switch
        {
            "execute" or "integer" => ":42\r\n"u8.ToArray(),
            "string" or "generic" => "$2\r\n42\r\n"u8.ToArray(),
            "load-source" or "load-library" => "$6\r\nsample\r\n"u8.ToArray(),
            "list" => "*0\r\n"u8.ToArray(),
            "dump" => "$2\r\n42\r\n"u8.ToArray(),
            "stats" => "*4\r\n+running_script\r\n$-1\r\n+engines\r\n*0\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        await using var server = new FakeRespServer(reply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var function = RespireFunction.Create("function");
        switch (route)
        {
            case "execute": using (await InspectFunctionOwner(client.Functions.ExecuteAsync(function))) { } break;
            case "integer": await InspectFunctionOwner(client.Functions.ExecuteIntegerAsync(function)); break;
            case "string": await InspectFunctionOwner(client.Functions.ExecuteStringAsync(function)); break;
            case "generic": await InspectFunctionOwner(client.Functions.ExecuteAsync<int>(function)); break;
            case "load-source": await InspectFunctionOwner(client.Functions.LoadAsync("source")); break;
            case "load-library": await InspectFunctionOwner(client.Functions.LoadAsync(RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1"))); break;
            case "list": await InspectFunctionOwner(client.Functions.ListAsync()); break;
            case "delete": await InspectFunctionOwner(client.Functions.DeleteAsync("sample")); break;
            case "flush": await InspectFunctionOwner(client.Functions.FlushAsync()); break;
            case "dump": await InspectFunctionOwner(client.Functions.DumpAsync()); break;
            case "restore": await InspectFunctionOwner(client.Functions.RestoreAsync(default)); break;
            default: await InspectFunctionOwner(client.Functions.StatsAsync()); break;
        }
        await Assert.That(capture.Items).IsEmpty();
    }

    private static async Task<T> InspectFunctionOwner<T>(ValueTask<T> pending)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var source = typeof(ValueTask<T>).GetField("_obj", flags)!.GetValue(pending);
        await Assert.That(source is DispatchResponseSource<T>).IsTrue();
        // Inspect after physical completion but before consuming the final caller response.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!pending.IsCompleted) await Task.Delay(1, deadline.Token);
        var observation = (ErrorObservation.FinalOwner)typeof(DispatchResponseSource<T>).GetField("_owner", flags)!.GetValue(source)!;
        await Assert.That(observation.IsEmpty).IsTrue();
        return await pending;
    }

    [Test]
    public async Task WarmFunctionOwnershipAddsNoSuccessAllocation()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        for (var index = 0; index < 10; index++) { _ = MeasureFunctionOwner(false); _ = MeasureFunctionOwner(true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (MeasureFunctionOwner(false), MeasureFunctionOwner(true)));
        await Assert.That(measured.Item1).IsEqualTo(0L);
        await Assert.That(measured.Item2).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items).IsEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureFunctionOwner(bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            _ = DispatchResponseSource<long>.Run(42L, static (value, _) => new ValueTask<long>(value)).GetAwaiter().GetResult();
            var spanOwner = DispatchResponseSource<RespireResult>.Start();
            spanOwner.Attach(new ValueTask<RespireResult>(default(RespireResult))).GetAwaiter().GetResult();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
