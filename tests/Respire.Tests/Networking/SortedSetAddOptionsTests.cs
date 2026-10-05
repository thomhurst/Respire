using TUnit.Core;

namespace Respire.Tests.Networking;

public class SortedSetAddOptionsTests
{
    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task EveryLegalCombination_WritesFlagsAndParsesResults(string mode)
    {
        foreach (var condition in new[] { "", "NX", "XX", "GT", "LT", "XX GT", "XX LT" })
        foreach (var changed in new[] { false, true })
        {
            var flags = (condition + (changed ? " CH" : "")).Trim();
            var options = Options(flags);
            byte[][] replies = mode == "transaction"
                ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
                    "+QUEUED\r\n"u8.ToArray(), "*3\r\n:1\r\n:2\r\n$3\r\n2.5\r\n"u8.ToArray()]
                : [":1\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), "$3\r\n2.5\r\n"u8.ToArray()];
            await using var server = new FakeRespServer(replies);
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            var view = client.WithKeyPrefix("tenant:");
            if (mode == "immediate")
            {
                await Assert.That(await view.SortedSets.AddAsync("key", options, "one", 1.5)).IsTrue();
                await Assert.That(await view.SortedSets.AddAsync("key", options, ("one", 1.5), ("two", 2))).IsEqualTo(2);
                await Assert.That(await view.SortedSets.IncrementAsync("key", options, "one", 1)).IsEqualTo(2.5);
            }
            else
            {
                using var batch = view.CreateBatch();
                await using var transaction = view.CreateTransaction();
                IRespireCommandQueue queue = mode == "batch" ? batch : transaction;
                var single = queue.SortedSets.Add("key", options, "one", 1.5);
                var many = queue.SortedSets.Add("key", options, ("one", 1.5), ("two", 2));
                var increment = queue.SortedSets.Increment("key", options, "one", 1);
                if (mode == "batch") await batch.ExecuteAsync();
                else await transaction.CommitAsync();
                await Assert.That(single.Result).IsTrue();
                await Assert.That(many.Result).IsEqualTo(2);
                await Assert.That(increment.Result).IsEqualTo(2.5);
            }
            var prefix = "ZADD tenant:key " + (flags.Length == 0 ? "" : flags + " ");
            await Assert.That(server.ReceivedCommands.Where(c => c.StartsWith("ZADD ")).SequenceEqual(
                [prefix + "1.5 one", prefix + "1.5 one 2 two", prefix + "INCR 1 one"])).IsTrue();
        }
    }

    [Test]
    public async Task InvalidFlagsAndEmptyEntries_RejectBeforeSendingOrQueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        var valid = new HashSet<int> { 0, 1, 2, 4, 8, 6, 10, 16, 17, 18, 20, 24, 22, 26 };
        for (var bits = 0; bits <= 32; bits++)
        {
            if (valid.Contains(bits)) continue;
            var options = (RespireSortedSetAddOptions)bits;
            await Assert.That(async () => await client.SortedSets.AddAsync("key", options, "member", 1)).Throws<ArgumentException>();
            await Assert.That(async () => await client.SortedSets.AddAsync("key", options, ("member", 1))).Throws<ArgumentException>();
            await Assert.That(async () => await client.SortedSets.IncrementAsync("key", options, "member", 1)).Throws<ArgumentException>();
            foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
            {
                await Assert.That(() => queue.SortedSets.Add("key", options, "member", 1)).Throws<ArgumentException>();
                await Assert.That(() => queue.SortedSets.Add("key", options, ("member", 1))).Throws<ArgumentException>();
                await Assert.That(() => queue.SortedSets.Increment("key", options, "member", 1)).Throws<ArgumentException>();
            }
        }
        await Assert.That(async () => await client.SortedSets.AddAsync("key", RespireSortedSetAddOptions.None, [])).Throws<ArgumentException>();
        await Assert.That(() => batch.SortedSets.Add("key", RespireSortedSetAddOptions.None, [])).Throws<ArgumentException>();
        await Assert.That(() => transaction.SortedSets.Add("key", RespireSortedSetAddOptions.None, [])).Throws<ArgumentException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task ConditionalIncrement_ParsesNull(string mode)
    {
        byte[][] replies = mode == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n$-1\r\n"u8.ToArray()]
            : ["$-1\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        double? result;
        if (mode == "immediate")
            result = await client.SortedSets.IncrementAsync("key", RespireSortedSetAddOptions.Nx, "member", 1);
        else
        {
            using var batch = client.CreateBatch();
            await using var transaction = client.CreateTransaction();
            IRespireCommandQueue queue = mode == "batch" ? batch : transaction;
            var pending = queue.SortedSets.Increment("key", RespireSortedSetAddOptions.Nx, "member", 1);
            if (mode == "batch") await batch.ExecuteAsync();
            else await transaction.CommitAsync();
            result = pending.Result;
        }
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task DeferredEntriesAreSnapshotted_AndMembersRemainBinarySafe()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] member = [0xff, 0, 0x80];
        (RespireValue Member, double Score)[] entries = [(member, 1.5)];
        using var batch = client.CreateBatch();
        var pending = batch.SortedSets.Add("key", RespireSortedSetAddOptions.Nx, entries);
        entries[0] = ("changed", 99);
        await batch.ExecuteAsync();
        await Assert.That(pending.Result).IsEqualTo(1);
        await Assert.That(server.ReceivedArguments.Single().Last().SequenceEqual(member)).IsTrue();
        await Assert.That(System.Text.Encoding.UTF8.GetString(server.ReceivedArguments.Single()[3])).IsEqualTo("1.5");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task NewOverloads_ForwardCancellation(int method)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { received.TrySetResult(); return true; } };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var options = RespireSortedSetAddOptions.Nx;
        Task pending = method switch
        {
            0 => client.SortedSets.AddAsync("key", options, (RespireValue)"member", 1, cancellation.Token).AsTask(),
            1 => client.SortedSets.AddAsync<bool>("key", options, true, 1, cancellation.Token).AsTask(),
            2 => client.SortedSets.AddAsync("key", options, [("member", 1)], cancellation.Token).AsTask(),
            _ => client.SortedSets.IncrementAsync("key", options, "member", 1, cancellation.Token).AsTask(),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }

    private static RespireSortedSetAddOptions Options(string flags)
    {
        var options = RespireSortedSetAddOptions.None;
        foreach (var flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options |= Enum.Parse<RespireSortedSetAddOptions>(flag, ignoreCase: true);
        return options;
    }
}
