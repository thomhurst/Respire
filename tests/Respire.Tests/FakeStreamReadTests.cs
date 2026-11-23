using Respire.Protocol;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeStreamReadTests
{
    [Test]
    [Arguments(2, false)]
    [Arguments(3, true)]
    public async Task BatchSizeGroupReaderStopsYieldingBufferedEntriesOnCancellation(int protocol, bool replay)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        var first = await client.Streams.AddAsync("events", ("f", "first"));
        var second = await client.Streams.AddAsync("events", ("f", "second"));
        await client.Streams.CreateGroupAsync("events", "g", RespireStreamId.Beginning);

        if (replay)
        {
            await client.Streams.ReadGroupOnceAsync("events", "g", "worker", new() { Count = 2 });
        }

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entries = replay
            ? client.Streams.ReadGroupAsync("events", "g", "worker", RespireStreamId.Beginning, batchSize: 2,
                cancellationToken: cancel.Token)
            : client.Streams.ReadGroupAsync("events", "g", "worker", batchSize: 2, cancellationToken: cancel.Token);
        await using var reader = entries.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Id).IsEqualTo(first);
        await reader.Current.AckAsync();

        cancel.Cancel();
        await Assert.That(async () => await reader.MoveNextAsync()).Throws<OperationCanceledException>();
        var pending = (await client.Streams.PendingAsync("events", "g")).Single();
        await Assert.That(pending.Id).IsEqualTo(second);
        await Assert.That(pending.Consumer).IsEqualTo("worker");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExplicitMaximumCountIsNotTreatedAsUnspecified(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await client.Streams.AddAsync("events", new RespireStreamAddOptions { Id = "1-0" }, ("f", "value"));
        await Assert.That(async () =>
        {
            using var reply = await client.ExecuteAsync("XREAD", "COUNT", long.MaxValue, "MAXCOUNT", 1, "STREAMS", "events", "0");
        }).ThrowsExactly<RespireServerException>();
        var entries = await client.Streams.ReadAsync(new RespireStreamReadOptions { MaxCount = 1 }, "events");
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LimitsPreservePendingEntriesAndQueuedReads(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });

#pragma warning restore RESP010
        foreach (var key in new[] { "one", "two" })
        {
            for (var i = 1; i <= 3; i++)
            {
                await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = $"{i}-0" }, ("f", new string('x', 100)));
            }

            await client.Streams.CreateGroupAsync(key, "g", "0");
        }

        var options = new RespireStreamReadOptions { Count = 2, MaxCount = 3 };
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Streams.ReadGroup([("one", ">"), ("two", ">")], "g", "c", options);
        await transaction.CommitAsync();
        await Assert.That(pending.Result.SelectMany(x => x.Entries).Count()).IsEqualTo(3);
        var history = await client.Streams.ReadGroupAsync([("one", "0"), ("two", "0")], "g", "c", new RespireStreamReadOptions { MaxSize = 1 });
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
    public async Task DeletedPendingEntryConsumesHistoryCountWithoutRedelivery(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using RespireClient client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            Protocol = (RespireProtocol)protocol
        });
#pragma warning restore RESP010
        await client.Streams.AddAsync("events", new RespireStreamAddOptions { Id = "1000-0" }, ("f", "deleted"));
        await client.Streams.AddAsync("events", new RespireStreamAddOptions { Id = "1000-1" }, ("f", "live"));
        await client.Streams.CreateGroupAsync("events", "g", "0-0");
        await client.Streams.ReadGroupOnceAsync("events", "g", "worker");
        await client.Streams.RemoveAsync("events", "1000-0");
        var elapsed = TimeSpan.FromSeconds(1);
        clock.Advance(elapsed);

        using RespireResult reply = await client.ExecuteAsync("XREADGROUP", "GROUP", "g", "worker",
            "COUNT", 1, "STREAMS", "events", "0-0");
        RespireResult page = protocol == 2 ? reply[0][1] : reply[1];
        await Assert.That(page.Count).IsEqualTo(1);
        await Assert.That(page[0][0].AsString()).IsEqualTo("1000-0");
        await Assert.That(page[0][1].NullWireType)
            .IsEqualTo(protocol == 2 ? RespireDataType.Array : RespireDataType.Null);

        RespireStreamPendingEntry[] pending = await client.Streams.PendingAsync("events", "g");
        await Assert.That(pending.Length).IsEqualTo(2);
        await Assert.That(pending[0].DeliveryCount).IsEqualTo(1);
        await Assert.That(pending[0].IdleTime).IsEqualTo(elapsed);
        await Assert.That(pending[1].DeliveryCount).IsEqualTo(1);
        await Assert.That(pending[1].IdleTime).IsEqualTo(elapsed);

        RespireStreamEntry[] next = await client.Streams.ReadGroupOnceAsync("events", "g", "worker",
            new RespireStreamReadOptions { Count = 1 }, startAt: "1000-0");
        await Assert.That(next.Length).IsEqualTo(1);
        await Assert.That(next[0].Id).IsEqualTo((RespireStreamId)"1000-1");
        await Assert.That(next[0].GetString("f")).IsEqualTo("live");
        await Assert.That(await client.Streams.AcknowledgeAsync("events", "g", "1000-0")).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PendingRangeUsesMillisecondBoundsInclusively(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using RespireClient client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            Protocol = (RespireProtocol)protocol,
        });
#pragma warning restore RESP010

        string[] ids = ["999-0", "1000-0", "1000-1", "1000-18446744073709551615", "1001-0"];

        foreach (string id in ids)
        {
            await client.Streams.AddAsync("events", new RespireStreamAddOptions { Id = id }, ("f", "value"));
        }

        await client.Streams.CreateGroupAsync("events", "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupAsync([("events", ">")], "g", "c");

        RespireStreamPendingEntry[] byMillisecond = await client.Streams.PendingAsync("events", "g", "1000", "1000");
        await Assert.That(byMillisecond.Select(entry => entry.Id.ToString()))
            .IsEquivalentTo(["1000-0", "1000-1", "1000-18446744073709551615"], CollectionOrdering.Matching);

        RespireStreamPendingEntry[] exclusive = await client.Streams.PendingAsync("events", "g", "(1000", "(1000");
        await Assert.That(exclusive.Single().Id).IsEqualTo(new RespireStreamId("1000-1"));

        RespireStreamPendingEntry[] byFullId = await client.Streams.PendingAsync("events", "g", "1000", "1000-0");
        await Assert.That(byFullId.Single().Id).IsEqualTo(new RespireStreamId("1000-0"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BlockingReadWakesAndCancellationDoesNotStallOtherTraffic(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol, Connections = 1 });
#pragma warning restore RESP010
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new RespireStreamReadOptions { MaxCount = 1, MaxSize = 1, WaitFor = Timeout.InfiniteTimeSpan };
        var read = client.Streams.ReadAsync(options, "events", cancellationToken: timeout.Token).AsTask();
        await client.Streams.AddAsync("events", new RespireStreamAddOptions { Id = "1-0" }, ("f", "first"));
        await Assert.That((await read)[0].Id).IsEqualTo((RespireStreamId)"1-0");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var blocked = client.Streams.ReadAsync(options, "events", "1-0", cancel.Token).AsTask();
        await Assert.That(await client.SetAsync("ordinary", "responsive", cancellationToken: timeout.Token)).IsTrue();
        cancel.Cancel();
        await Assert.That(async () => await blocked).Throws<OperationCanceledException>();
        var empty = await client.Streams.ReadAsync(options with { WaitFor = TimeSpan.FromMilliseconds(1) }, "missing", cancellationToken: timeout.Token);
        await Assert.That(empty).IsEmpty();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BlockedGroupReadersReceiveEntriesInRegistrationOrder(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = new RespireStreamReadOptions { Count = 1, WaitFor = Timeout.InfiniteTimeSpan };
        using var observed = server.InjectFault("XREADGROUP", RespireFakeFault.Delay(TimeSpan.Zero), occurrences: null);
        await client.Streams.CreateGroupAsync("events", "g", "0", createStream: true);
        await client.Streams.CreateGroupAsync("other", "g", "0", createStream: true);

        for (int round = 0; round < 10; round++)
        {
            var first = client.Streams.ReadGroupAsync([("other", ">"), ("events", ">")], "g", "first", options,
                deadline.Token).AsTask();
            await WaitForExecutionCountAsync(observed, round * 2 + 1, deadline.Token);
            var second = client.Streams.ReadGroupOnceAsync("events", "g", "second", options,
                cancellationToken: deadline.Token).AsTask();
            await WaitForExecutionCountAsync(observed, round * 2 + 2, deadline.Token);

            RespireStreamId firstId = await client.Streams.AddAsync("events", ("f", "first"));
            RespireStreamId secondId = await client.Streams.AddAsync("events", ("f", "second"));

            var firstPage = await first;
            await Assert.That(firstPage.Single().Entries.Single().Id).IsEqualTo(firstId);
            await Assert.That((await second).Single().Id).IsEqualTo(secondId);
        }
    }

    [Test]
    public async Task UnrelatedAppendLeavesBlockingReadPending()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var observed = server.InjectFault("XREAD", RespireFakeFault.Delay(TimeSpan.Zero));
        var waiting = client.Streams.ReadAsync(new RespireStreamReadOptions { WaitFor = Timeout.InfiniteTimeSpan },
            "events", "$", deadline.Token).AsTask();
        await WaitForExecutionCountAsync(observed, 1, deadline.Token);

        await client.SetAsync("events", "temporary", cancellationToken: deadline.Token);
        await client.Streams.AddAsync("other", ("f", "unrelated"));
        await Assert.That(async () => await waiting.WaitAsync(TimeSpan.FromMilliseconds(250), deadline.Token))
            .ThrowsExactly<TimeoutException>();

        await client.Keys.DeleteAsync("events");
        RespireStreamId id = await client.Streams.AddAsync("events", ("f", "intended"));
        await Assert.That((await waiting).Single().Id).IsEqualTo(id);
    }

    [Test]
    public async Task DeletingStreamWakesBlockedGroupReader()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var observed = server.InjectFault("XREADGROUP", RespireFakeFault.Delay(TimeSpan.Zero));
        await client.Streams.CreateGroupAsync("events", "g", "0", createStream: true);
        var waiting = client.Streams.ReadGroupOnceAsync("events", "g", "worker",
            new RespireStreamReadOptions { WaitFor = Timeout.InfiniteTimeSpan },
            cancellationToken: deadline.Token).AsTask();
        await WaitForExecutionCountAsync(observed, 1, deadline.Token);

        await client.Keys.DeleteAsync("events");
        await Assert.That(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token))
            .ThrowsExactly<RespireServerException>().WithMessage("NOGROUP No such consumer group");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GroupReadOnlyRegistersConsumersOnVisitedStreams(bool limitBySize)
    {
        await using var server = new RespireFakeServer(null, createConsumersOnEmptyReads: true);
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());

#pragma warning restore RESP010
        foreach (string key in new[] { "first", "second" })
        {
            await client.Streams.AddAsync(key, ("f", "value"));
            await client.Streams.CreateGroupAsync(key, "g", "0");
        }

        var options = limitBySize ? new RespireStreamReadOptions { MaxSize = 1 }
            : new RespireStreamReadOptions { Count = 1, MaxCount = 1 };
        var reply = await client.Streams.ReadGroupAsync([("first", ">"), ("second", ">")], "g", "worker", options);
        await Assert.That(reply.Single().Entries.Length).IsEqualTo(1);
        var skipped = (await client.Streams.GroupInfoAsync("second")).Single();
        await Assert.That(skipped.Consumers).IsEqualTo(0);
        await Assert.That(skipped.Pending).IsEqualTo(0);
    }

    [Test]
    public async Task AutoClaimAcceptsBareBoundariesButRejectsExclusiveBoundaries()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        await client.Streams.AddAsync("events", new RespireStreamAddOptions { Id = "1-0" }, ("f", "value"));
        await client.Streams.CreateGroupAsync("events", "g", "0");
        await client.Streams.ReadGroupOnceAsync("events", "g", "worker");

        using var minimum = await client.ExecuteAsync("XAUTOCLAIM", "events", "g", "next", 0, "-", "JUSTID");
        await Assert.That(minimum[1][0].AsString()).IsEqualTo("1-0");
        using var maximum = await client.ExecuteAsync("XAUTOCLAIM", "events", "g", "next", 0, "+", "JUSTID");
        await Assert.That(maximum[1].Count).IsEqualTo(0);

        foreach (string cursor in new[] { "(-", "(+" })
        {
            await Assert.That(async () =>
            {
                using var reply = await client.ExecuteAsync("XAUTOCLAIM", "events", "g", "next", 0, cursor, "JUSTID");
            }).ThrowsExactly<RespireServerException>();
        }
    }

    private static async Task WaitForExecutionCountAsync(RespireFakeFaultScope scope, int count, CancellationToken cancellationToken)
    {
        while (scope.ExecutionCount < count)
        {
            await Task.Delay(1, cancellationToken);
        }
    }
}
