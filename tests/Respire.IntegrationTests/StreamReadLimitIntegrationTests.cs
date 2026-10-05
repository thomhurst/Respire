using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public sealed class Redis810StreamReadContainer() : StandaloneRedisTestContainer("redis:8.10-alpine");

[ClassDataSource<Redis810StreamReadContainer>(Shared = SharedType.PerTestSession)]
public class StreamReadLimitIntegrationTests(Redis810StreamReadContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task LimitsSurviveBlockingWakeAndCancellation(int protocol, bool group)
    {
        var name = $"limits-block-{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol, Connections = 1, ClientName = name });
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (group) await client.Streams.CreateGroupAsync(name, "g", "0");
        var options = new StreamReadOptions { MaxCount = 1, MaxSize = 1, WaitFor = Timeout.InfiniteTimeSpan };
        var read = (group ? client.Streams.ReadGroupOnceAsync(name, "g", "c", options, cancellationToken: timeout.Token)
            : client.Streams.ReadAsync(options, name, cancellationToken: timeout.Token)).AsTask();
        await WaitForBlockedAsync();
        (await client.SetAsync(name + ":ordinary", "responsive", cancellationToken: timeout.Token)).Should().BeTrue();
        await using (var transaction = observer.CreateTransaction())
        {
            for (var i = 1; i <= 3; i++)
                _ = transaction.Streams.Add(name, new StreamAddOptions { Id = $"{i}-0" }, ("f", new string('x', 100)));
            await transaction.CommitAsync(timeout.Token);
        }
        (await read).Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"1-0");
        if (group) (await client.Streams.ReadGroupOnceAsync(name, "g", "c")).Should().HaveCount(2);
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
                if (clients.Any(x => x.Name == name && x.Flags.Contains('b'))) return;
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
        await using var fakeClient = await RespireClient.ConnectAsync(fake.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await using var redisClient = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        var prefix = $"parity:{Guid.NewGuid():N}:";
        var clients = new[] { fakeClient.WithKeyPrefix(prefix), redisClient.WithKeyPrefix(prefix) };
        foreach (var client in clients)
            foreach (var key in new[] { "one", "two" })
                for (var i = 1; i <= 3; i++)
                    await client.Streams.AddAsync(key, new StreamAddOptions { Id = $"{i}-0" }, ("f", new string('x', 50)));
        foreach (var budget in new[] { 1, 50, 100, 120, 160, 200, 250, 300, 400, 500, 1000 })
        {
            var options = new StreamReadOptions { Count = 2, MaxCount = 3, MaxSize = budget };
            foreach (var group in new[] { false, true })
            {
                var results = new List<string[]>();
                foreach (var client in clients)
                {
                    var name = $"g-{budget}";
                    if (group)
                        foreach (var key in new[] { "one", "two" }) await client.Streams.CreateGroupAsync(key, name, "0");
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
            with { Protocol = (RespProtocol)protocol });
        var client = owner.WithKeyPrefix($"limits:{Guid.NewGuid():N}:");
        foreach (var key in new[] { "one", "two" })
        {
            for (var i = 1; i <= 3; i++)
                await client.Streams.AddAsync(key, new StreamAddOptions { Id = $"{i}-0" }, ("value", new string('x', 200)));
            if (group) await client.Streams.CreateGroupAsync(key, "g", "0");
        }
        (RespireKey Key, RespireStreamId After)[] streams = [("one", group ? ">" : "0"), ("two", group ? ">" : "0")];
        var options = new StreamReadOptions { Count = 2, MaxCount = 3, MaxSize = 100_000 };
        RespireStreamReadResult[] result;
        if (surface == 0) result = group ? await client.Streams.ReadGroupAsync(streams, "g", "c", options)
            : await client.Streams.ReadAsync(options, streams);
        else
        {
            using var batch = surface == 1 ? client.CreateBatch() : null;
            await using var transaction = surface == 2 ? client.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = group ? queue.Streams.ReadGroup(streams, "g", "c", options) : queue.Streams.Read(options, streams);
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            result = pending.Result;
        }
        result.SelectMany(x => x.Entries).Should().HaveCount(3);
        result[0].Entries.Should().HaveCount(2);
        result[1].Entries.Should().ContainSingle();
        var tiny = new StreamReadOptions { MaxSize = 1 };
        var oversized = group
            ? await client.Streams.ReadGroupAsync([("one", "0"), ("two", "0")], "g", "c", tiny)
            : await client.Streams.ReadAsync(tiny, streams);
        oversized.SelectMany(x => x.Entries).Should().ContainSingle();
        if (group)
        {
            var remaining = await client.Streams.ReadGroupAsync(streams, "g", "c", new StreamReadOptions { MaxCount = 1 });
            remaining.SelectMany(x => x.Entries).Should().ContainSingle();
            await remaining[0].Entries[0].AckAsync();
        }
    }
}
