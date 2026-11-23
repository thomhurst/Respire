using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamReadLimitIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LatestCursorReadsOneEntryPerStreamDirectlyAndFromQueues(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with
        { Protocol = (RespireProtocol)protocol });
        var prefix = $"latest:{Guid.NewGuid():N}:";
        var first = prefix + "one";
        var second = prefix + "two";

        foreach (var key in new[] { first, second })
        {
            await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "1-0" }, ("value", "old"));
            await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "2-0" }, ("value", "latest"));
        }

        (RespireKey Key, RespireStreamId After)[] streams =
            [(first, RespireStreamId.Max), (prefix + "missing", RespireStreamId.Max), (second, RespireStreamId.Max)];

        AssertLatest(await client.Streams.ReadAsync(streams, count: 2));

        using var batch = client.CreateBatch();
        var batched = batch.Streams.Read(streams, count: 2);
        await batch.ExecuteAsync();
        AssertLatest(batched.Result);

        await using var transaction = client.CreateTransaction();
        var queued = transaction.Streams.Read(streams, count: 2);
        await transaction.CommitAsync();
        AssertLatest(queued.Result);

        void AssertLatest(RespireStreamReadResult[] result)
        {
            result.Select(x => x.Key).Should().Equal((RespireKey)first, (RespireKey)second);

            foreach (var stream in result)
            {
                var entry = stream.Entries.Should().ContainSingle().Which;
                entry.Id.Should().Be((RespireStreamId)"2-0");
                entry.GetString("value").Should().Be("latest");
            }
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FakeMatchesRedisForMixedLatestCursors(int protocol)
    {
        await using var fake = new RespireFakeServer();
        await using var fakeClient = await RespireClient.ConnectAsync(fake.CreateOptions()
            with
        { Protocol = (RespireProtocol)protocol });
        await using var redisClient = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with
        { Protocol = (RespireProtocol)protocol });
        var prefix = $"latest-mixed:{Guid.NewGuid():N}:";

        foreach (var owner in new[] { redisClient, fakeClient })
        {
            var client = owner.WithKeyPrefix(prefix);

            foreach (string key in new[] { "latest", "numeric", "new" })
            {
                await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "1-0" }, ("f", "old"));
                await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "2-0" }, ("f", "latest"));
                await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "3-0" }, ("f", "deleted"));
            }

            await client.Streams.RemoveAsync("latest", "3-0");
            await client.Streams.CreateGroupAsync("empty", "g", "0", createStream: true);

            (RespireKey Key, RespireStreamId After)[] streams =
            [
                ("empty", "+"), ("latest", "+"), ("missing", "+"), ("numeric", "0"), ("new", "$")
            ];
            var options = new RespireStreamReadOptions { Count = 2, WaitFor = TimeSpan.FromSeconds(1) };
            var rows = await client.Streams.ReadAsync(options, streams);
            rows.Select(row => row.Key).Should().Equal((RespireKey)"latest", (RespireKey)"numeric");
            var latest = rows[0].Entries.Should().ContainSingle().Which;
            latest.Id.Should().Be((RespireStreamId)"2-0");
            latest.GetString("f").Should().Be("latest");
            rows[1].Entries.Select(entry => entry.Id).Should().Equal((RespireStreamId)"1-0", (RespireStreamId)"2-0");

            (await client.Streams.ReadAsync([("empty", "+"), ("missing", "+")])).Should().BeEmpty();
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FakeMatchesRedisWhenBlockingLatestCursorWakes(int protocol)
    {
        var name = $"latest-block-{Guid.NewGuid():N}";
        await using var fake = new RespireFakeServer();
        await using var fakeClient = await RespireClient.ConnectAsync(fake.CreateOptions()
            with
        { Protocol = (RespireProtocol)protocol });
        await using var redisClient = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with
        { Protocol = (RespireProtocol)protocol, ClientName = name });
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var observed = fake.InjectFault("XREAD", RespireFakeFault.Delay(TimeSpan.Zero), occurrences: null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        foreach (var client in new[] { redisClient, fakeClient })
        {
            await client.Streams.CreateGroupAsync(name, "g", "0", createStream: true);
            var shortWait = new RespireStreamReadOptions { WaitFor = TimeSpan.FromMilliseconds(25) };
            (await client.Streams.ReadAsync(shortWait, name, "+", deadline.Token)).Should().BeEmpty();

            var options = new RespireStreamReadOptions { Count = 2, WaitFor = Timeout.InfiniteTimeSpan };
            var read = client.Streams.ReadAsync(options, name, "+", deadline.Token).AsTask();

            while (!read.IsCompleted)
            {
                var blocked = client == fakeClient ? observed.ExecutionCount >= 2
                    : (await observer.Server.ClientsAsync(deadline.Token)).Any(info => info.Name == name && info.Flags.Contains('b'));

                if (blocked)
                {
                    break;
                }

                await Task.Delay(5, deadline.Token);
            }

            read.IsCompleted.Should().BeFalse();
            var writer = client == fakeClient ? fakeClient : observer;
            await using var transaction = writer.CreateTransaction();
            _ = transaction.Streams.Add(name, new RespireStreamAddOptions { Id = "1-0" }, ("f", "first"));
            _ = transaction.Streams.Add(name, new RespireStreamAddOptions { Id = "2-0" }, ("f", "second"));
            await transaction.CommitAsync(deadline.Token);

            var latest = (await read).Should().ContainSingle().Which;
            latest.Id.Should().Be((RespireStreamId)"2-0");
            latest.GetString("f").Should().Be("second");
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AcknowledgingAbsentGroupMatchesRedis(int protocol)
    {
        await using var fake = new Respire.Testing.RespireFakeServer();
        await using var fakeClient = await RespireClient.ConnectAsync(fake.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        await using var redisClient = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with
        { Protocol = (RespireProtocol)protocol });
        var key = $"ack-absent:{Guid.NewGuid():N}";

        foreach (var client in new[] { fakeClient, redisClient })
        {
            (await client.Streams.AcknowledgeAsync(key, "absent", "1-0")).Should().Be(0);
            await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "1-0" }, ("field", "value"));
            (await client.Streams.AcknowledgeAsync(key, "absent", "1-0")).Should().Be(0);
        }
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task LimitsSurviveBlockingWakeAndCancellation(int protocol, bool group)
    {
        var name = $"limits-block-{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with
        { Protocol = (RespireProtocol)protocol, Connections = 1, ClientName = name });
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        if (group)
        {
            await client.Streams.CreateGroupAsync(name, "g", "0");
        }

        var options = new RespireStreamReadOptions { MaxCount = 1, MaxSize = 1, WaitFor = Timeout.InfiniteTimeSpan };
        var read = (group ? client.Streams.ReadGroupOnceAsync(name, "g", "c", options, cancellationToken: timeout.Token)
            : client.Streams.ReadAsync(options, name, cancellationToken: timeout.Token)).AsTask();
        await WaitForBlockedAsync();
        (await client.SetAsync(name + ":ordinary", "responsive", cancellationToken: timeout.Token)).Should().BeTrue();

        await using (var transaction = observer.CreateTransaction())
        {
            for (var i = 1; i <= 3; i++)
            {
                _ = transaction.Streams.Add(name, new RespireStreamAddOptions { Id = $"{i}-0" }, ("f", new string('x', 100)));
            }

            await transaction.CommitAsync(timeout.Token);
        }

        (await read).Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"1-0");

        if (group)
        {
            (await client.Streams.ReadGroupOnceAsync(name, "g", "c")).Should().HaveCount(2);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var next = (group ? client.Streams.ReadGroupOnceAsync(name, "g", "c", options, cancellationToken: cancel.Token)
            : client.Streams.ReadAsync(options, name, "3-0", cancel.Token)).AsTask();
        await WaitForBlockedAsync();
        cancel.Cancel();
        Func<Task> canceled = async () => await next;
        await canceled.Should().ThrowAsync<OperationCanceledException>();

        async Task WaitForBlockedAsync()
        {
            while (true)
            {
                var clients = await observer.Server.ClientsAsync(timeout.Token);

                if (clients.Any(x => x.Name == name && x.Flags.Contains('b')))
                {
                    return;
                }

                await Task.Delay(10, timeout.Token);
            }
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FakeMatchesRedisAcrossReplyBudgets(int protocol)
    {
        await using var fake = new Respire.Testing.RespireFakeServer();
        await using var fakeClient = await RespireClient.ConnectAsync(fake.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        await using var redisClient = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with
        { Protocol = (RespireProtocol)protocol });
        var prefix = $"parity:{Guid.NewGuid():N}:";
        var clients = new[] { fakeClient.WithKeyPrefix(prefix), redisClient.WithKeyPrefix(prefix) };

        foreach (var client in clients)
        {
            foreach (var key in new[] { "one", "two" })
            {
                for (var i = 1; i <= 3; i++)
                {
                    await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = $"{i}-0" }, ("f", new string('x', 50)));
                }
            }
        }

        foreach (var budget in new[] { 1, 50, 100, 120, 160, 200, 250, 300, 400, 500, 1000 })
        {
            var options = new RespireStreamReadOptions { Count = 2, MaxCount = 3, MaxSize = budget };

            foreach (var group in new[] { false, true })
            {
                var results = new List<string[]>();

                foreach (var client in clients)
                {
                    var name = $"g-{budget}";

                    if (group)
                    {
                        foreach (var key in new[] { "one", "two" })
                        {
                            await client.Streams.CreateGroupAsync(key, name, "0");
                        }
                    }

                    var rows = group
                        ? await client.Streams.ReadGroupAsync([("one", ">"), ("two", ">")], name, "c", options)
                        : await client.Streams.ReadAsync(options, [("one", "0"), ("two", "0")]);
                    results.Add(rows.SelectMany(row => row.Entries.Select(entry => $"{row.Key}:{entry.Id}")).ToArray());
                }

                results[0].Should().Equal(results[1], $"protocol {protocol}, MAXSIZE {budget}, group {group}");
            }
        }
    }

    [Test]
    [Arguments(2, 0, false)]
    [Arguments(3, 0, false)]
    [Arguments(2, 1, false)]
    [Arguments(3, 2, false)]
    [Arguments(2, 0, true)]
    [Arguments(3, 0, true)]
    [Arguments(2, 1, true)]
    [Arguments(3, 2, true)]
    public async Task CumulativeLimitsApplyAcrossStreamsAndQueues(int protocol, int surface, bool group)
    {
        await using var owner = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with
        { Protocol = (RespireProtocol)protocol });
        var client = owner.WithKeyPrefix($"limits:{Guid.NewGuid():N}:");

        foreach (var key in new[] { "one", "two" })
        {
            for (var i = 1; i <= 3; i++)
            {
                await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = $"{i}-0" }, ("value", new string('x', 200)));
            }

            if (group)
            {
                await client.Streams.CreateGroupAsync(key, "g", "0");
            }
        }

        (RespireKey Key, RespireStreamId After)[] streams = [("one", group ? ">" : "0"), ("two", group ? ">" : "0")];
        var options = new RespireStreamReadOptions { Count = 2, MaxCount = 3, MaxSize = 100_000 };
        RespireStreamReadResult[] result;

        if (surface == 0)
        {
            result = group ? await client.Streams.ReadGroupAsync(streams, "g", "c", options)
            : await client.Streams.ReadAsync(options, streams);
        }
        else
        {
            using var batch = surface == 1 ? client.CreateBatch() : null;
            await using var transaction = surface == 2 ? client.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = group ? queue.Streams.ReadGroup(streams, "g", "c", options) : queue.Streams.Read(options, streams);

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await batch!.ExecuteAsync();
            }

            result = pending.Result;
        }

        result.SelectMany(x => x.Entries).Should().HaveCount(3);
        result[0].Entries.Should().HaveCount(2);
        result[1].Entries.Should().ContainSingle();
        var tiny = new RespireStreamReadOptions { MaxSize = 1 };
        var oversized = group
            ? await client.Streams.ReadGroupAsync([("one", "0"), ("two", "0")], "g", "c", tiny)
            : await client.Streams.ReadAsync(tiny, streams);
        oversized.SelectMany(x => x.Entries).Should().ContainSingle();

        if (group)
        {
            var remaining = await client.Streams.ReadGroupAsync(streams, "g", "c", new RespireStreamReadOptions { MaxCount = 1 });
            remaining.SelectMany(x => x.Entries).Should().ContainSingle();
            await remaining[0].Entries[0].AckAsync();
        }
    }
}
