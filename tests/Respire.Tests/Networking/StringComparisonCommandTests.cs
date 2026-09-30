using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StringComparisonCommandTests
{
    private sealed record Document(int Version);

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task GenericComparison_SerializesValuesButKeepsOperandRaw(string mode)
    {
        var response = Encoding.UTF8.GetBytes("$13\r\n{\"Version\":1}\r\n");
        byte[][] replies = mode == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), [.. "*1\r\n"u8, .. response]] : [response];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] operand = "{\"Version\":1}"u8.ToArray();
        var condition = RespireValueCondition.EqualTo(operand);
        Document? result;
        if (mode == "immediate")
        {
            Array.Clear(operand);
            result = await client.Strings.GetAndSetConditionalAsync("key", new Document(2), condition);
        }
        else
        {
            using var batch = mode == "batch" ? client.CreateBatch() : null;
            await using var transaction = mode == "transaction" ? client.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = queue.Strings.GetAndSetConditional("key", new Document(2), condition);
            Array.Clear(operand);
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            result = pending.Result;
        }
        await Assert.That(result).IsEqualTo(new Document(1));
        await Assert.That(server.ReceivedCommands.Where(x => x is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["SET key {\"Version\":2} IFEQ {\"Version\":1} GET"]);
    }

    [Test]
    public async Task ExistingDefaultNxAndXxBindings_AreUnchanged()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.Strings.SetAsync("raw", (RespireValue)"value", default);
        await client.Strings.SetAsync("typed", "value", default);
        await client.Strings.SetAsync("nx", (RespireValue)"value", default, SetWhen.NotExists);
        await client.Strings.SetAsync("xx", "value", default, SetWhen.Exists);
        using var batch = client.CreateBatch();
        _ = batch.Strings.Set("raw", (RespireValue)"value", default);
        _ = batch.Strings.Set("typed", "value", default);
        _ = batch.Strings.Set("nx", (RespireValue)"value", default, SetWhen.NotExists);
        _ = batch.Strings.Set("xx", "value", default, SetWhen.Exists);
        await batch.ExecuteAsync();
        string[] expected = ["SET raw value", "SET typed value", "SET nx value NX", "SET xx value XX"];
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(expected.Concat(expected));
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task Comparisons_PreserveWireOptionsAndOwnedResults(string mode)
    {
        byte[][] results = [FakeRespServer.OkReply, "$-1\r\n"u8.ToArray(),
            [.. "$2\r\n"u8, 0xff, 0, .. "\r\n"u8], "$3\r\nold\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), "$16\r\n0123456789abcdef\r\n"u8.ToArray()];
        byte[][] replies = mode == "transaction"
            ? [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), results.Length),
                [.. Encoding.ASCII.GetBytes($"*{results.Length}\r\n"), .. results.SelectMany(x => x)]]
            : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        byte[] operand = "old"u8.ToArray();
        var equal = RespireValueCondition.EqualTo(operand);
        Array.Fill(operand, (byte)'x');
        var different = RespireValueCondition.NotEqualTo("old");
        var digest = RespireValueCondition.DigestEqualTo("0123456789abcdef");
        var otherDigest = RespireValueCondition.DigestNotEqualTo("fedcba9876543210");
        byte[]? owned;
        if (mode == "immediate")
        {
            await Assert.That(await view.Strings.SetConditionalAsync("a", (RespireValue)"new", equal, RespireExpiry.In(TimeSpan.FromSeconds(5)))).IsTrue();
            await Assert.That(await view.Strings.SetConditionalAsync("b", 42, different, RespireExpiry.Keep)).IsFalse();
            owned = await view.Strings.GetAndSetConditionalAsync<byte[]>("c", [1], digest, RespireExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(2000000000000)));
            await Assert.That(await view.Strings.GetAndSetConditionalAsync("d", (RespireValue)"next", otherDigest)).IsEqualTo("old");
            await Assert.That(await view.Strings.DeleteConditionalAsync("e", equal)).IsTrue();
            await Assert.That(await view.Strings.DeleteIfEqualAsync("f", "old")).IsFalse();
            await Assert.That(await view.Strings.DigestAsync("g")).IsEqualTo("0123456789abcdef");
        }
        else
        {
            using var batch = mode == "batch" ? view.CreateBatch() : null;
            await using var transaction = mode == "transaction" ? view.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var a = queue.Strings.SetConditional("a", (RespireValue)"new", equal, RespireExpiry.In(TimeSpan.FromSeconds(5)));
            var b = queue.Strings.SetConditional("b", 42, different, RespireExpiry.Keep);
            var c = queue.Strings.GetAndSetConditional<byte[]>("c", [1], digest, RespireExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(2000000000000)));
            var d = queue.Strings.GetAndSetConditional("d", (RespireValue)"next", otherDigest);
            var e = queue.Strings.DeleteConditional("e", equal);
            var f = queue.Strings.DeleteIfEqual("f", "old");
            var g = queue.Strings.Digest("g");
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            await Assert.That(a.Result).IsTrue();
            await Assert.That(b.Result).IsFalse();
            owned = c.Result;
            await Assert.That(d.Result).IsEqualTo("old");
            await Assert.That(e.Result).IsTrue();
            await Assert.That(f.Result).IsFalse();
            await Assert.That(g.Result).IsEqualTo("0123456789abcdef");
        }
        await client.DisposeAsync();
        await Assert.That(owned).IsEquivalentTo(new byte[] { 0xff, 0 });
        await Assert.That(server.ReceivedCommands.Where(x => x is not "MULTI" and not "EXEC")).IsEquivalentTo([
            "SET tenant:a new IFEQ old PX 5000", "SET tenant:b 42 IFNE old KEEPTTL",
            "SET tenant:c \u0001 IFDEQ 0123456789abcdef PXAT 2000000000000 GET",
            "SET tenant:d next IFDNE fedcba9876543210 GET", "DELEX tenant:e IFEQ old",
            "DELIFEQ tenant:f old", "DIGEST tenant:g"]);
    }

    [Test]
    public async Task InvalidInputs_DoNotSendOrQueue()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(() => RespireValueCondition.EqualTo(default)).Throws<ArgumentException>();
        await Assert.That(() => RespireValueCondition.NotEqualTo(default)).Throws<ArgumentException>();
        await Assert.That(() => RespireValueCondition.DigestEqualTo(null!)).Throws<ArgumentException>();
        await Assert.That(() => RespireValueCondition.DigestNotEqualTo(null!)).Throws<ArgumentException>();
        var condition = RespireValueCondition.EqualTo("");
        await Assert.That(async () => await client.Strings.SetConditionalAsync("k", (RespireValue)"v", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Strings.SetConditionalAsync("k", default(RespireValue), condition)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Strings.GetAndSetConditionalAsync("k", (RespireValue)"v", condition, RespireExpiry.Persist)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Strings.DeleteConditionalAsync("k", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Strings.DeleteIfEqualAsync("k", default)).Throws<ArgumentException>();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (var strings in new[] { batch.Strings, transaction.Strings })
        {
            await Assert.That(() => strings.SetConditional("k", (RespireValue)"v", null!)).Throws<ArgumentNullException>();
            await Assert.That(() => strings.SetConditional("k", default(RespireValue), condition)).Throws<ArgumentException>();
            await Assert.That(() => strings.GetAndSetConditional("k", (RespireValue)"v", condition, RespireExpiry.Persist)).Throws<ArgumentException>();
            await Assert.That(() => strings.DeleteConditional("k", null!)).Throws<ArgumentNullException>();
            await Assert.That(() => strings.DeleteIfEqual("k", default)).Throws<ArgumentException>();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments("set")]
    [Arguments("get-set")]
    [Arguments("delete")]
    [Arguments("delete-equal")]
    [Arguments("digest")]
    public async Task Cancellation_ReachesPendingCommands(string operation)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { received.TrySetResult(); return true; } };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var equal = RespireValueCondition.EqualTo("old");
        Task pending = operation switch
        {
            "set" => client.Strings.SetConditionalAsync("k", (RespireValue)"v", equal, cancellationToken: cancellation.Token).AsTask(),
            "get-set" => client.Strings.GetAndSetConditionalAsync("k", (RespireValue)"v", equal, cancellationToken: cancellation.Token).AsTask(),
            "delete" => client.Strings.DeleteConditionalAsync("k", equal, cancellation.Token).AsTask(),
            "delete-equal" => client.Strings.DeleteIfEqualAsync("k", "old", cancellation.Token).AsTask(),
            _ => client.Strings.DigestAsync("k", cancellation.Token).AsTask(),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task Cluster_RoutesPrefixedKeyAndDoesNotTreatOperandsAsKeys(string mode)
    {
        byte[][] results = [FakeRespServer.OkReply, ":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), "$-1\r\n"u8.ToArray()];
        byte[][] replies = mode == "transaction"
            ? [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), 4), [.. "*4\r\n"u8, .. results.SelectMany(x => x)]]
            : results;
        await using var owner = new FakeRespServer(replies);
        var slot = ClusterHash.GetSlot("tenant:{compare}:key");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        const string key = "{compare}:key";
        var condition = RespireValueCondition.EqualTo("{other}:old");
        if (mode == "immediate")
        {
            await view.Strings.SetConditionalAsync(key, (RespireValue)"{different}:new", condition);
            await view.Strings.DeleteConditionalAsync(key, condition);
            await view.Strings.DeleteIfEqualAsync(key, "{other}:old");
            await view.Strings.DigestAsync(key);
        }
        else
        {
            using var batch = mode == "batch" ? view.CreateBatch() : null;
            await using var transaction = mode == "transaction" ? view.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            _ = queue.Strings.SetConditional(key, (RespireValue)"{different}:new", condition);
            _ = queue.Strings.DeleteConditional(key, condition);
            _ = queue.Strings.DeleteIfEqual(key, "{other}:old");
            _ = queue.Strings.Digest(key);
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
        }
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands.Where(x => x is not "MULTI" and not "EXEC")).IsEquivalentTo([
            "SET tenant:{compare}:key {different}:new IFEQ {other}:old", "DELEX tenant:{compare}:key IFEQ {other}:old",
            "DELIFEQ tenant:{compare}:key {other}:old", "DIGEST tenant:{compare}:key"]);
    }
}
