using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class FunctionCommandTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SpanCallsConsumeCollectionsAndPreserveBinaryArguments(bool readOnly)
    {
        await using var server = new FakeRespServer(":42\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");
        RespireKey[] keys = ["ignored", "key"];
        byte[] binary = [0, 255, 128];
        RespireValue[] args = ["ignored", binary];
        var pending = client.Functions.ExecuteSpanAsync(RespireFunction.Create("function", readOnly), keys.AsSpan(1), args.AsSpan(1));
        keys[1] = "changed"; args[1] = "changed";
        using var result = await pending;
        await Assert.That(result.AsInteger()).IsEqualTo(42);
        var actual = server.ReceivedArguments.Single();
        await Assert.That(Encoding.UTF8.GetString(actual[0])).IsEqualTo(readOnly ? "FCALL_RO" : "FCALL");
        await Assert.That(Encoding.UTF8.GetString(actual[1])).IsEqualTo("function");
        await Assert.That(Encoding.UTF8.GetString(actual[2])).IsEqualTo("1");
        await Assert.That(Encoding.UTF8.GetString(actual[3])).IsEqualTo("tenant:key");
        await Assert.That(actual[4]).IsEquivalentTo(binary, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredOwnedResultsOutliveContainersAndClient(bool transaction)
    {
        byte[] binary = [0, 255, 128];
        byte[] bulk = [.. "$3\r\n"u8, .. binary, .. "\r\n"u8];
        byte[][] replies = [[.. "*2\r\n"u8, .. bulk, .. "*1\r\n"u8, .. bulk], bulk, FakeRespServer.OkReply];
        if (transaction) replies = [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), 3), [.. "*3\r\n"u8, .. replies.SelectMany(x => x)]];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        RespireResult owned;
        byte[] dump;
        using (var batch = client.WithKeyPrefix("tenant:").CreateBatch())
        {
            await using var tx = client.WithKeyPrefix("tenant:").CreateTransaction();
            var functions = transaction ? tx.Functions : batch.Functions;
            var call = functions.Execute(RespireFunction.Create("function", true), ["key"], [binary]);
            var backup = functions.Dump();
            var restore = functions.Restore(binary, FunctionRestorePolicy.Replace);
            if (transaction) await tx.CommitAsync(); else await batch.ExecuteAsync();
            owned = call.Result; dump = backup.Result;
            await Assert.That(restore.Result).IsTrue();
        }
        await client.DisposeAsync();
        using (owned)
        {
            await Assert.That(owned[0].AsBytes()).IsEquivalentTo(binary, CollectionOrdering.Matching);
            await Assert.That(owned[1][0].AsBytes()).IsEquivalentTo(binary, CollectionOrdering.Matching);
        }
        await Assert.That(dump).IsEquivalentTo(binary, CollectionOrdering.Matching);
        var restoreWire = server.ReceivedArguments.Single(a => Encoding.UTF8.GetString(a[0]) == "FUNCTION" && Encoding.UTF8.GetString(a[1]) == "RESTORE");
        await Assert.That(restoreWire[2]).IsEquivalentTo(binary, CollectionOrdering.Matching);
        await Assert.That(Encoding.UTF8.GetString(restoreWire[3])).IsEqualTo("REPLACE");
    }

    [Test]
    public async Task ClusterSlotValidationPrecedesQueueAndAdminDoesNotSelectSlot()
    {
        await using var client = RespireClient.Create(new RespireOptions { UseCluster = true, Endpoints = { new RespireEndpoint("localhost") } });
        var function = RespireFunction.Create("function");
        await Assert.That(async () => { using var result = await client.Functions.ExecuteAsync(function, ["{a}", "{b}"]); }).Throws<RespireServerException>();
        using var batch = client.CreateBatch();
        await Assert.That(() => batch.Functions.Execute(function, ["{a}", "{b}"])).Throws<RespireServerException>();
        await using var tx = client.CreateTransaction();
        await Assert.That(() => tx.Functions.Execute(function, ["{a}", "{b}"])).Throws<RespireServerException>();
        _ = tx.Functions.Load("#!lua name=library\nreturn 1");
        _ = tx.Functions.List("library", true);
        _ = tx.Functions.Restore(new byte[] { 1 });
        _ = tx.Functions.Flush(FunctionFlushMode.Async);
        _ = tx.Functions.Delete("library");
        _ = tx.Functions.Execute(function, ["{b}"]);
        await Assert.That(() => tx.Functions.Execute(function, ["{a}"])).Throws<InvalidOperationException>();
        var prefixed = client.WithKeyPrefix("{same}:");
        _ = FunctionCommands.CallCommand((RespireClient)prefixed, function, ["{a}", "{b}"], []);
        var noKeys = FunctionCommands.CallCommand(client, function, [], ["{argument}"]);
        await Assert.That(noKeys.TryGetClusterSlot(out _)).IsFalse();
    }

    [Test]
    public async Task InvalidInputsNeverReachServer()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(() => RespireFunction.Create(" ")).Throws<ArgumentException>();
        await Assert.That(() => RespireFunctionLibrary.Create("return 1")).Throws<ArgumentException>();
        await Assert.That(() => RespireFunctionLibrary.Create("#!lua name=x name=y")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Functions.LoadAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Functions.DeleteAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Functions.ListAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Functions.FlushAsync((FunctionFlushMode)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Functions.RestoreAsync(default, (FunctionRestorePolicy)99)).Throws<ArgumentOutOfRangeException>();
        using var batch = client.CreateBatch();
        await Assert.That(() => batch.Functions.Load(" ")).Throws<ArgumentException>();
        await Assert.That(() => batch.Functions.Flush((FunctionFlushMode)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => batch.Functions.Restore(default, (FunctionRestorePolicy)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationDoesNotReload(bool readOnly)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { arrived.TrySetResult(); return true; } };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var function = RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1").Function("function", readOnly);
        var pending = client.Functions.ExecuteIntegerAsync(function, cancellationToken: cancellation.Token).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task MissingFunctionReloadIsBoundedAndOtherErrorsNeverRetry()
    {
        var missing = "-ERR Function not found\r\n"u8.ToArray();
        await using var server = new FakeRespServer(missing, "*0\r\n"u8.ToArray(), "$6\r\nsample\r\n"u8.ToArray(), missing, "-ERR runtime failure\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var function = RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1").Function("function");
        await Assert.That(async () => await client.Functions.ExecuteIntegerAsync(function)).Throws<RespireServerException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(4);
        await Assert.That(async () => await client.Functions.ExecuteIntegerAsync(function)).Throws<RespireServerException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(5);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MalformedMetadataThrowsProtocolError(bool stats)
    {
        await using var server = new FakeRespServer("*1\r\n$3\r\nodd\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        if (stats) await Assert.That(async () => await client.Functions.StatsAsync()).Throws<RespireProtocolException>();
        else await Assert.That(async () => await client.Functions.ListAsync()).Throws<RespireProtocolException>();
    }

    [Test]
    public async Task ReadOnlyCallsAndLibraryAdministrationPreserveCacheButWritesInvalidate()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        RespireKey key = "key";
        var token = cache.BeginRead(in key); var value = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in value, allowInsert: true);
        foreach (var operation in new[] { "FCALL_RO", "FUNCTION LOAD", "FUNCTION LIST", "FUNCTION DELETE", "FUNCTION FLUSH", "FUNCTION DUMP", "FUNCTION RESTORE", "FUNCTION STATS" })
        {
            var command = new Cmd1(new Verb(operation), "argument");
            var fence = cache.BeforeCommand(operation, in command); cache.CompleteMutation(in fence);
        }
        await Assert.That(cache.Count).IsEqualTo(1);
        var write = new Cmd1(new Verb("FCALL"), "function");
        var mutation = cache.BeforeCommand("FCALL", in write); cache.CompleteMutation(in mutation);
        await Assert.That(cache.Count).IsEqualTo(0);
    }
    [Test]
    public async Task CancellationWhileWaitingForReloadGateDoesNotLoad()
    {
        await using var server = new FakeRespServer("-ERR Function not found\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var library = RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1");
        var gate = library.ReloadState(client.Core).Gate;
        await gate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var pending = client.Functions.ExecuteIntegerAsync(library.Function("function"), cancellationToken: cancellation.Token).AsTask();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);
            cancellation.Cancel();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await Assert.That(server.CommandsSeen).IsEqualTo(1);
        }
        finally { gate.Release(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunningStatisticsOwnBinaryCommandArguments(bool resp3)
    {
        var command = RespValue.Array([RespValue.BulkString("FCALL"u8.ToArray()), RespValue.BulkString(new byte[] { 0, 255 })]);
        var running = Map([Text("name"), Text("function"), Text("command"), command, Text("duration_ms"), RespValue.Integer(123)]);
        var counts = Map([Text("libraries_count"), RespValue.Integer(1), Text("functions_count"), RespValue.Integer(2)]);
        var reply = Map([Text("running_script"), running, Text("engines"), Map([Text("LUA"), counts])]);
        var stats = FunctionResponseReader.Stats(in reply);
        reply.Dispose();
        await Assert.That(stats.RunningFunction!.Name).IsEqualTo("function");
        await Assert.That(stats.RunningFunction.DurationMilliseconds).IsEqualTo(123);
        await Assert.That(stats.RunningFunction.Command[1]).IsEquivalentTo(new byte[] { 0, 255 }, CollectionOrdering.Matching);
        await Assert.That(stats.Engines["LUA"]).IsEqualTo(new RespireFunctionEngineStats(1, 2));

        RespValue Map(RespValue[] values)
        {
            if (!resp3) return RespValue.Array(values);
            var rented = RespirePools.ValueArrays.Rent(values.Length);
            values.CopyTo(rented, 0);
            return RespValue.PooledAggregate(RespDataType.Map, rented, values.Length);
        }
        static RespValue Text(string value) => RespValue.BulkString(Encoding.UTF8.GetBytes(value));
    }

    [Test]
    public async Task ConcurrentMissingCallsShareOneFilteredReload()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray(), "$6\r\nsample\r\n"u8.ToArray(), ":42\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("FCALL ", StringComparison.Ordinal)) return false;
                var number = Interlocked.Increment(ref calls);
                if (number == 2) arrived.TrySetResult();
                return number <= 2;
            }
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var function = RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1").Function("function");
        var first = client.Functions.ExecuteIntegerAsync(function).AsTask();
        var second = client.Functions.ExecuteIntegerAsync(function).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync("-ERR Function not found\r\n-ERR Function not found\r\n"u8.ToArray());
        await Assert.That(await Task.WhenAll(first, second)).IsEquivalentTo(new long[] { 42, 42 });
        await Assert.That(server.ReceivedCommands.Count(command => command == "FUNCTION LIST LIBRARYNAME sample WITHCODE")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("FUNCTION LOAD ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    [Arguments("ERR Function not found in user data")]
    [Arguments("ERR function not found")]
    [Arguments("ERR user callback: Function not found")]
    public async Task SimilarApplicationErrorsMustNotTriggerReplay(string message)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes($"-{message}\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var function = RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1").Function("function");
        await Assert.That(async () => await client.Functions.ExecuteIntegerAsync(function)).Throws<RespireServerException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task LibraryFilterEscapesGlobMetacharacters()
    {
        await Assert.That(FunctionCommands.EscapeLibraryPattern(@"a[b]*?\c")).IsEqualTo(@"a\[b\]\*\?\\c");
    }

}
