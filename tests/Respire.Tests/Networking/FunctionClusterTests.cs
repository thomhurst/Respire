using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class FunctionClusterTests
{
    [Test]
    public async Task AdministrationMutationsVisitEveryPrimary()
    {
        byte[][] responses = ["$6\r\nsample\r\n"u8.ToArray(), FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply];
        await using var first = new FakeRespServer(responses);
        await using var second = new FakeRespServer(responses);
        await using var seed = new FakeRespServer(Topology(first.Port, second.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        await Assert.That(await client.Functions.LoadAsync("source", true)).IsEqualTo("sample");
        await client.Functions.DeleteAsync("sample");
        await client.Functions.RestoreAsync("binary"u8.ToArray(), FunctionRestorePolicy.Replace);
        await client.Functions.FlushAsync(FunctionFlushMode.Async);
        await Assert.That(first.ReceivedCommands).IsEquivalentTo([
            "FUNCTION LOAD REPLACE source", "FUNCTION DELETE sample", "FUNCTION RESTORE binary REPLACE", "FUNCTION FLUSH ASYNC"]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(first.ReceivedCommands);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CallsRouteByPrefixedKeyAndNotFunctionOrArgument(int mode)
    {
        await using var first = new FakeRespServer(":41\r\n"u8.ToArray());
        byte[][] replies = mode == 2
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n:42\r\n"u8.ToArray()]
            : [":42\r\n"u8.ToArray()];
        await using var second = new FakeRespServer(replies);
        await using var seed = new FakeRespServer(Topology(first.Port, second.Port));
        await using var owner = await RespireClient.ConnectAsync(Options(seed.Port));
        var client = owner.WithKeyPrefix("{foo}:");
        var function = RespireFunction.Create("function", true);
        long result;
        if (mode == 0) result = await client.Functions.ExecuteIntegerAsync(function, ["key"], ["argument"]);
        else if (mode == 1)
        {
            using var batch = client.CreateBatch();
            var pending = batch.Functions.ExecuteInteger(function, ["key"], ["argument"]);
            await batch.ExecuteAsync(); result = pending.Result;
        }
        else
        {
            await using var transaction = client.CreateTransaction();
            var pending = transaction.Functions.ExecuteInteger(function, ["key"], ["argument"]);
            await transaction.CommitAsync(); result = pending.Result;
        }
        await Assert.That(ClusterHash.GetSlot("foo")).IsGreaterThan(8191);
        await Assert.That(result).IsEqualTo(42);
        await Assert.That(first.CommandsSeen).IsEqualTo(0);
        await Assert.That(second.ReceivedCommands).Contains("FCALL_RO function 1 {foo}:key argument");
    }

    [Test]
    public async Task ReloadLoadsMissingLibraryAcrossPrimariesAndRetriesOnce()
    {
        var source = "#!lua name=sample\nreturn 1";
        await using var first = new FakeRespServer("*0\r\n"u8.ToArray(), "$6\r\nsample\r\n"u8.ToArray(),
            FunctionLibraryList(source));
        await using var second = new FakeRespServer("-ERR Function not found\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(),
            "$6\r\nsample\r\n"u8.ToArray(), FunctionLibraryList(source), ":42\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(Topology(first.Port, second.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var function = RespireFunctionLibrary.Create(source).Function("function");
        await Assert.That(await client.Functions.ExecuteIntegerAsync(function, ["{foo}:key"])).IsEqualTo(42);
        await Assert.That(first.ReceivedCommands).IsEquivalentTo([
            "FUNCTION LIST LIBRARYNAME sample WITHCODE", $"FUNCTION LOAD {source}", "FUNCTION LIST LIBRARYNAME sample WITHCODE"]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo([
            "FCALL function 1 {foo}:key", "FUNCTION LIST LIBRARYNAME sample WITHCODE", $"FUNCTION LOAD {source}",
            "FUNCTION LIST LIBRARYNAME sample WITHCODE", "FCALL function 1 {foo}:key"]);
    }

    [Test]
    public async Task RegisteredFunctionWaitsForReplicaPropagationBeforeRetrying()
    {
        var source = "#!lua name=sample\nreturn 1";
        await using var replica = new FakeRespServer(FakeRespServer.OkReply,
            "-ERR Function not found\r\n"u8.ToArray(), "-ERR Function not found\r\n"u8.ToArray(), ":42\r\n"u8.ToArray());
        await using var primary = new FakeRespServer(FunctionLibraryList(source));
        await using var seed = new FakeRespServer(TopologyWithReplica(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var function = RespireFunctionLibrary.Create(source).Function("function", readOnly: true);

        var result = await client.WithReadFrom(RespireReadFrom.Replica)
            .Functions.ExecuteIntegerAsync(function, ["{foo}:key"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
            ["FUNCTION LIST LIBRARYNAME sample WITHCODE"]);
        await Assert.That(seed.ReceivedCommands).Contains("CLUSTER SLOTS");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(
            ["READONLY", "FCALL_RO function 1 {foo}:key", "FCALL_RO function 1 {foo}:key", "FCALL_RO function 1 {foo}:key"]);
        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplicaFunctionPropagationHonorsConfiguredTimeout(bool disabled)
    {
        var source = "#!lua name=sample\nreturn 1";
        await using var replica = new FakeRespServer(FakeRespServer.OkReply,
            "-ERR Function not found\r\n"u8.ToArray());
        await using var primary = new FakeRespServer(FunctionLibraryList(source));
        await using var seed = new FakeRespServer(TopologyWithReplica(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port) with
        {
            CommandTimeout = disabled ? null : TimeSpan.FromMilliseconds(100),
            ClusterTopologyRefreshInterval = null,
        });
        var function = RespireFunctionLibrary.Create(source).Function("function", readOnly: true);
        using var cancellation = new CancellationTokenSource();
        var pending = client.WithReadFrom(RespireReadFrom.Replica).Functions
            .ExecuteIntegerAsync(function, ["{foo}:key"], cancellationToken: cancellation.Token).AsTask();
        if (disabled)
        {
            // Exceed the former hard-coded ten-second fallback, then stop via the caller.
            await Task.Delay(TimeSpan.FromSeconds(11));
            await Assert.That(pending.IsCompleted).IsFalse();
            cancellation.Cancel();
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
        }
        else
        {
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<RespireTimeoutException>();
        }
    }

    [Test]
    public async Task RegisteredLibraryMissingRequestedFunctionDoesNotPollReplica()
    {
        var source = "#!lua name=sample\nreturn 1";
        await using var replica = new FakeRespServer(FakeRespServer.OkReply,
            "-ERR Function not found\r\n"u8.ToArray());
        await using var primary = new FakeRespServer(FunctionLibraryList(source, "different"));
        await using var seed = new FakeRespServer(TopologyWithReplica(primary.Port, replica.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var function = RespireFunctionLibrary.Create(source).Function("function", readOnly: true);

        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica)
            .Functions.ExecuteIntegerAsync(function, ["{foo}:key"])).Throws<RespireServerException>();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
            ["FUNCTION LIST LIBRARYNAME sample WITHCODE"]);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(
            ["READONLY", "FCALL_RO function 1 {foo}:key"]);
    }

    [Test]
    public async Task FanOutObservesEveryReplyBeforeSurfacingFailure()
    {
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new FakeRespServer { SuppressReply = _ => { firstArrived.TrySetResult(); return true; } };
        await using var second = new FakeRespServer { SuppressReply = _ => { secondArrived.TrySetResult(); return true; } };
        await using var seed = new FakeRespServer(Topology(first.Port, second.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var pending = client.Functions.FlushAsync().AsTask();
        await Task.WhenAll(firstArrived.Task, secondArrived.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await first.SendRawAsync("-ERR rejected\r\n"u8.ToArray());
        await Assert.That(pending.IsCompleted).IsFalse();
        await second.SendRawAsync(FakeRespServer.OkReply);
        await Assert.That(async () => await pending).Throws<RespireServerException>();
    }

    private static RespireOptions Options(int seedPort) => new()
    {
        Protocol = RespProtocol.Resp2,
        UseCluster = true, Endpoints = { new RespireEndpoint("127.0.0.1", seedPort) }, Connections = 1
    };
    private static byte[] Topology(int first, int second) => Encoding.ASCII.GetBytes(
        $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first}\r\n" +
        $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second}\r\n");

    private static byte[] TopologyWithReplica(int primary, int replica) => Encoding.ASCII.GetBytes(
        $"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary}\r\n" +
        $"*2\r\n$9\r\n127.0.0.1\r\n:{replica}\r\n");

    private static byte[] FunctionLibraryList(string source, string functionName = "function") => Encoding.ASCII.GetBytes(
        $"*1\r\n*8\r\n+library_name\r\n$6\r\nsample\r\n+engine\r\n$3\r\nLUA\r\n" +
        $"+functions\r\n*1\r\n*6\r\n+name\r\n${Encoding.ASCII.GetByteCount(functionName)}\r\n{functionName}\r\n+description\r\n$-1\r\n" +
        $"+flags\r\n*0\r\n+library_code\r\n${Encoding.UTF8.GetByteCount(source)}\r\n{source}\r\n");
}
