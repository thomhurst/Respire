using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeStreamReadTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExplicitMaximumCountIsNotTreatedAsUnspecified(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("f", "value"));
        await Assert.That(async () =>
        {
            using var reply = await client.ExecuteAsync("XREAD", "COUNT", long.MaxValue, "MAXCOUNT", 1, "STREAMS", "events", "0");
        }).ThrowsExactly<RespireServerException>();
        var entries = await client.Streams.ReadAsync(new StreamReadOptions { MaxCount = 1 }, "events");
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LimitsPreservePendingEntriesAndQueuedReads(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        foreach (var key in new[] { "one", "two" })
        {
            for (var i = 1; i <= 3; i++) await client.Streams.AddAsync(key, new StreamAddOptions { Id = $"{i}-0" }, ("f", new string('x', 100)));
            await client.Streams.CreateGroupAsync(key, "g", "0");
        }
        var options = new StreamReadOptions { Count = 2, MaxCount = 3 };
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Streams.ReadGroup([("one", ">"), ("two", ">")], "g", "c", options);
        await transaction.CommitAsync();
        await Assert.That(pending.Result.SelectMany(x => x.Entries).Count()).IsEqualTo(3);
        var history = await client.Streams.ReadGroupAsync([("one", "0"), ("two", "0")], "g", "c", new StreamReadOptions { MaxSize = 1 });
        await Assert.That(history.SelectMany(x => x.Entries).Count()).IsEqualTo(1);
        await history[0].Entries[0].AckAsync();
        var afterAck = await client.Streams.ReadGroupAsync([("one", "0"), ("two", "0")], "g", "c");
        await Assert.That(afterAck.SelectMany(x => x.Entries).Count()).IsEqualTo(2);
        var newEntries = await client.Streams.ReadGroupAsync([("one", ">"), ("two", ">")], "g", "c");
        await Assert.That(newEntries.SelectMany(x => x.Entries).Count()).IsEqualTo(3);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BlockingReadWakesAndCancellationDoesNotStallOtherTraffic(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol, Connections = 1 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new StreamReadOptions { MaxCount = 1, MaxSize = 1, WaitFor = Timeout.InfiniteTimeSpan };
        var read = client.Streams.ReadAsync(options, "events", cancellationToken: timeout.Token).AsTask();
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("f", "first"));
        await Assert.That((await read)[0].Id).IsEqualTo((RespireStreamId)"1-0");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var blocked = client.Streams.ReadAsync(options, "events", "1-0", cancel.Token).AsTask();
        await Assert.That(await client.SetAsync("ordinary", "responsive", cancellationToken: timeout.Token)).IsTrue();
        cancel.Cancel();
        await Assert.That(async () => await blocked).Throws<OperationCanceledException>();
        var empty = await client.Streams.ReadAsync(options with { WaitFor = TimeSpan.FromMilliseconds(1) }, "missing", cancellationToken: timeout.Token);
        await Assert.That(empty).IsEmpty();
    }
}
