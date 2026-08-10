using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

/// <summary>
/// Multi-item commands take their <see cref="CancellationToken"/> through a sibling overload,
/// because a <c>params</c> parameter must come last. These tests prove the token actually reaches
/// the send rather than only appearing in the signature.
/// </summary>
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class MultiItemCancellationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task MultiItemCommands_WithCancelledToken_Throw()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var token = cts.Token;

        RespireKey[] keys = ["cancel:a", "cancel:b"];
        RespireValue[] values = ["one", "two"];
        string[] fields = ["f1", "f2"];
        (RespireKey Key, RespireValue Value)[] pairs = [("cancel:a", "one"), ("cancel:b", "two")];

        await Throws(() => client.DeleteAsync(keys, token));
        await Throws(() => client.Keys.DeleteAsync(keys, token));
        await Throws(() => client.Keys.UnlinkAsync(keys, token));
        await Throws(() => client.Keys.TouchAsync(keys, token));
        await Throws(() => client.Strings.GetManyAsync(keys, token));
        await Throws(() => client.Strings.SetManyAsync(pairs, token));
        await Throws(() => client.Strings.SetManyExpireAsync(
            TimeSpan.FromMinutes(1), SetWhen.Always, pairs, token));
        await Throws(() => client.Hashes.GetManyAsync("cancel:hash", fields, token));
        await Throws(() => client.Hashes.DeleteAsync("cancel:hash", fields, token));
        await Throws(() => client.Lists.RightPushAsync("cancel:list", values, token));
        await Throws(() => client.Sets.AddAsync("cancel:set", values, token));
        await Throws(() => client.SortedSets.AddAsync("cancel:zset", [new SortedSetEntry("m", 1)], token));
        await Throws(() => client.HyperLogLog.AddAsync("cancel:hll", values, token));
        await Throws(() => client.Streams.AddAsync("cancel:stream", [("f1", "one")], token));
    }

    [Test]
    public async Task MultiItemCommands_WithLiveToken_Succeed()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        RespireKey[] keys = ["live:a", "live:b"];
        (RespireKey Key, RespireValue Value)[] pairs = [("live:a", "one"), ("live:b", "two")];

        await client.Strings.SetManyAsync(pairs, token);
        (await client.Strings.GetManyAsync(keys, token)).Should().Equal("one", "two");
        (await client.Keys.TouchAsync(keys, token)).Should().Be(2);

        (await client.Sets.AddAsync("live:set", ["x", "y"], token)).Should().Be(2);
        (await client.Sets.RemoveAsync("live:set", ["x"], token)).Should().Be(1);

        (await client.Lists.RightPushAsync("live:list", ["a", "b"], token)).Should().Be(2);
        (await client.SortedSets.AddAsync("live:zset", [new SortedSetEntry("m", 1)], token)).Should().Be(1);

        (await client.Hashes.SetAsync("live:hash", [("f1", "one"), ("f2", "two")], token)).Should().Be(2);
        (await client.Hashes.GetManyAsync("live:hash", ["f1", "f2"], token)).Should().Equal("one", "two");
        (await client.Hashes.DeleteAsync("live:hash", ["f1"], token)).Should().Be(1);

        (await client.Keys.DeleteAsync(keys, token)).Should().Be(2);
    }

    /// <summary>A pre-cancelled token must surface as cancellation, not as a completed command.</summary>
    private static async Task Throws<T>(Func<ValueTask<T>> call)
    {
        var act = async () => await call();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task Throws(Func<ValueTask> call)
    {
        var act = async () => await call();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
