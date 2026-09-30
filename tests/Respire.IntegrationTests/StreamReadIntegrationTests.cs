using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamReadIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, 0)]
    [Arguments(3, 0)]
    [Arguments(2, 1)]
    [Arguments(3, 1)]
    [Arguments(2, 2)]
    [Arguments(3, 2)]
    public async Task ReadsOwnBinaryKeysAndValuesAcrossSurfaces(int protocol, int surface)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix("租户:");
        RespireKey key = new byte[] { 0xff, 0, 0x42 };
        await view.Streams.AddAsync(key, new StreamAddOptions { Id = "1-0" }, ("字段", (RespireValue)new byte[] { 0xff, 0 }));
        await view.Streams.AddAsync(key, new StreamAddOptions { Id = "2-0" }, ("字段", "second"));
        await view.Streams.AddAsync("other", new StreamAddOptions { Id = "3-0" }, ("value", "third"));
        (RespireKey Key, RespireStreamId After)[] streams = [("missing", "0"), (key, "0"), ("other", "0")];
        RespireStreamReadResult[] result;
        if (surface == 0) result = await view.Streams.ReadAsync(streams, count: 1);
        else
        {
            using var batch = surface == 1 ? view.CreateBatch() : null;
            await using var transaction = surface == 2 ? view.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = queue.Streams.Read(streams, count: 1);
            var single = queue.Streams.Read(key, "1-0");
            var missing = queue.Streams.Read("missing");
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            result = pending.Result;
            single.Result.Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"2-0");
            missing.Result.Should().BeEmpty();
        }
        (await view.Streams.ReadAsync(key, "1-0")).Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"2-0");
        (await view.Streams.ReadAsync("missing")).Should().BeEmpty();
        (await view.Streams.ReadAsync("missing", waitFor: TimeSpan.FromMilliseconds(1))).Should().BeEmpty();
        await client.DisposeAsync();
        result.Select(x => x.Key).Should().Equal(key, (RespireKey)"other");
        result[0].Entries.Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"1-0");
        result[0].Entries[0]["字段"].Should().Equal(0xff, 0);
        result[1].Entries.Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"3-0");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BlockingReadLeavesOrdinaryTrafficResponsiveAndCancellationReleasesLease(int protocol)
    {
        var name = $"stream-block-{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}&connections=1&clientName={name}");
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var read = client.Streams.ReadAsync("events", waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancel.Token).AsTask();
        await WaitForBlockedAsync(observer, name, timeout.Token);
        (await client.SetAsync("ordinary", "responsive", cancellationToken: timeout.Token)).Should().BeTrue();
        read.IsCompleted.Should().BeFalse();
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("value", "first"));
        (await read).Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"1-0");
        var canceled = client.Streams.ReadAsync("events", "1-0", waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancel.Token).AsTask();
        await WaitForBlockedAsync(observer, name, timeout.Token);
        cancel.Cancel();
        Func<Task> wait = async () => await canceled;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        (await client.Streams.ReadAsync("events", waitFor: TimeSpan.FromSeconds(1), cancellationToken: timeout.Token))
            .Should().ContainSingle();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EnumerationResumesAfterKilledConnectionAndDisposesBetweenEntries(int protocol)
    {
        var name = $"stream-resume-{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}&clientName={name}");
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("value", "first"));
        await using (var reader = client.Streams.ReadAllAsync("events", cancellationToken: timeout.Token).GetAsyncEnumerator())
        {
            (await reader.MoveNextAsync()).Should().BeTrue();
            reader.Current.Id.Should().Be((RespireStreamId)"1-0");
            var next = reader.MoveNextAsync().AsTask();
            var blocked = await WaitForBlockedAsync(observer, name, timeout.Token);
            (await observer.Server.KillClientAsync(blocked.Id, cancellationToken: timeout.Token)).Should().BeTrue();
            // Entries persist while the reader has no active connection, then are delivered from its saved cursor.
            await observer.Streams.AddAsync("events", new StreamAddOptions { Id = "2-0" }, ("value", "second"));
            await observer.Streams.AddAsync("events", new StreamAddOptions { Id = "3-0" }, ("value", "third"));
            (await next).Should().BeTrue();
            reader.Current.Id.Should().Be((RespireStreamId)"2-0");
            (await reader.MoveNextAsync()).Should().BeTrue();
            reader.Current.Id.Should().Be((RespireStreamId)"3-0");
        }
        (await client.Streams.ReadAsync("events", "2-0", waitFor: TimeSpan.FromSeconds(1), cancellationToken: timeout.Token))
            .Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"3-0");
    }

    private static async Task<RespireServerClientInfo> WaitForBlockedAsync(IRespireClient observer, string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            var clients = await observer.Server.ClientsAsync(cancellationToken);
            var blocked = clients.FirstOrDefault(x => x.Name == name && x.Flags.Contains('b') && x.Command == "xread");
            if (blocked is not null) return blocked;
            await Task.Delay(10, cancellationToken);
        }
    }
}
