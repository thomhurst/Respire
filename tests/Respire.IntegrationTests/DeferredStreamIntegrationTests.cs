using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DeferredStreamIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task NonBlockingStreamCommandsRoundTrip(int protocol, bool transactional)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var prefix = $"deferred-stream:{Guid.NewGuid():N}:";
        var view = client.WithKeyPrefix(prefix);
        await view.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("value", "first"));
        await view.Streams.CreateGroupAsync("events", "workers", RespireStreamId.Beginning);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var entry in view.Streams.ReadGroupAsync("events", "workers", "alice", cancellationToken: timeout.Token))
        {
            entry.Id.Should().Be((RespireStreamId)"1-0");
            break;
        }
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var state = queue.Set("state", "appended");
        byte[] payload = [0xff, 0, 0x80];
        var explicitId = queue.Streams.Add("events", new StreamAddOptions { Id = "2-0" }, ("value", (RespireValue)payload));
        var automatic = queue.Streams.Add("events", ("value", "latest"));
        var absent = queue.Streams.Add("missing", new StreamAddOptions { CreateStream = false }, ("value", "unused"));
        var count = queue.Streams.Count("events");
        var entries = queue.Streams.Range("events", count: 2);
        var descending = queue.Streams.Range("events", count: 2, descending: true);
        var acknowledged = queue.Streams.Acknowledge("events", "workers", "1-0");
        var removed = queue.Streams.Remove("events", "2-0");
        var trimmed = queue.Streams.TrimByMaxLength("events", 1);
        var remaining = queue.Streams.Range("events");
        Array.Fill(payload, (byte)'x');
        if (transaction is not null) await transaction.CommitAsync(timeout.Token);
        else await batch!.ExecuteAsync(timeout.Token);
        state.Result.Should().BeTrue();
        explicitId.Result.Should().Be((RespireStreamId)"2-0");
        absent.Result.Should().BeNull();
        count.Result.Should().Be(3);
        entries.Result.Select(entry => entry.Id.ToString()).Should().Equal("1-0", "2-0");
        entries.Result[1]["value"].Should().Equal(0xff, 0, 0x80);
        descending.Result.Select(entry => entry.Id).Should().Equal(automatic.Result, (RespireStreamId)"2-0");
        acknowledged.Result.Should().Be(1);
        removed.Result.Should().Be(1);
        trimmed.Result.Should().Be(1);
        remaining.Result.Should().ContainSingle().Which.Id.Should().Be(automatic.Result);
        (await view.GetStringAsync("state")).Should().Be("appended");
        (await view.Streams.PendingSummaryAsync("events", "workers")).Count.Should().Be(0);
        (await client.Keys.ExistsAsync(prefix + "events")).Should().BeTrue();
        (await client.Keys.ExistsAsync(prefix + prefix + "events")).Should().BeFalse();
        await view.Keys.DeleteAsync("events", "state", "missing");
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task StreamErrorsDoNotHideOtherResults(int protocol, bool transactional)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var key = $"stream-error:{Guid.NewGuid():N}";
        await client.SetAsync(key, "wrong type");
        using var batch = transactional ? null : client.CreateBatch();
        await using var transaction = transactional ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var failed = queue.Streams.Count(key);
        var value = queue.GetString(key);
        Func<Task> execute = async () =>
        {
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
        };
        await execute.Should().ThrowAsync<RespireServerException>();
        failed.Error.Should().BeOfType<RespireServerException>();
        value.Result.Should().Be("wrong type");
        await client.Keys.DeleteAsync(key);
    }
}
