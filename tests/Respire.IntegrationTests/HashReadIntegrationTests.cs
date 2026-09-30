using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class HashReadIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(RespProtocol.Resp2, 0)]
    [Arguments(RespProtocol.Resp3, 0)]
    [Arguments(RespProtocol.Resp2, 1)]
    [Arguments(RespProtocol.Resp3, 1)]
    [Arguments(RespProtocol.Resp2, 2)]
    [Arguments(RespProtocol.Resp3, 2)]
    public async Task RandomFields_PreserveCountValuesAndMissingSemantics(RespProtocol protocol, int mode)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("typed-hash:");
        var key = $"hash-random:{Guid.NewGuid():N}";
        var missing = key + ":missing";
        await client.Hashes.SetAsync(key, ("a", "first"), ("b", "second"));

        var single = await ExecuteAsync(client, mode,
            () => client.Hashes.RandomFieldAsync(key), h => h.RandomField(key));
        single.Should().BeOneOf("a", "b");
        (await ExecuteAsync(client, mode,
            () => client.Hashes.RandomFieldAsync(missing), h => h.RandomField(missing))).Should().BeNull();

        foreach (var count in new long[] { 0, 10, -5 })
        {
            var fields = await ExecuteAsync(client, mode,
                () => client.Hashes.RandomFieldsAsync(key, count), h => h.RandomFields(key, count));
            var pairs = await ExecuteAsync(client, mode,
                () => client.Hashes.RandomFieldsWithValuesAsync(key, count), h => h.RandomFieldsWithValues(key, count));
            var expectedCount = count == 0 ? 0 : count > 0 ? 2 : 5;
            fields.Should().HaveCount(expectedCount).And.OnlyContain(f => f == "a" || f == "b");
            pairs.Should().HaveCount(expectedCount);
            foreach (var pair in pairs)
            {
                pair.Key.Should().BeOneOf("a", "b");
                pair.Value.Should().Be(pair.Key == "a" ? "first" : "second");
            }
            if (count > 0)
            {
                fields.Should().OnlyHaveUniqueItems();
                pairs.Select(p => p.Key).Should().OnlyHaveUniqueItems();
            }
            (await ExecuteAsync(client, mode,
                () => client.Hashes.RandomFieldsAsync(missing, count), h => h.RandomFields(missing, count)))
                .Should().BeEmpty();
            (await ExecuteAsync(client, mode,
                () => client.Hashes.RandomFieldsWithValuesAsync(missing, count), h => h.RandomFieldsWithValues(missing, count)))
                .Should().BeEmpty();
        }
    }

    [Test]
    [Arguments(RespProtocol.Resp2, 0)]
    [Arguments(RespProtocol.Resp3, 0)]
    [Arguments(RespProtocol.Resp2, 1)]
    [Arguments(RespProtocol.Resp3, 1)]
    [Arguments(RespProtocol.Resp2, 2)]
    [Arguments(RespProtocol.Resp3, 2)]
    public async Task Length_ReturnsByteLengthAndZeroForMissingFields(RespProtocol protocol, int mode)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("typed-hash:");
        var key = $"hash-length:{Guid.NewGuid():N}";
        await client.Hashes.SetAsync(key, ("text", "é🙂"), ("empty", ""), ("binary", new byte[] { 0, 255, 128 }));
        foreach (var (field, expected) in new[] { ("text", 6L), ("binary", 3L), ("empty", 0L), ("missing", 0L) })
        {
            (await ExecuteAsync(client, mode,
                () => client.Hashes.LengthAsync(key, field), h => h.Length(key, field))).Should().Be(expected);
        }
        (await ExecuteAsync(client, mode,
            () => client.Hashes.LengthAsync(key + ":missing", "text"), h => h.Length(key + ":missing", "text")))
            .Should().Be(0);
    }

    [Test]
    [Arguments(RespProtocol.Resp2, 0)]
    [Arguments(RespProtocol.Resp3, 0)]
    [Arguments(RespProtocol.Resp2, 1)]
    [Arguments(RespProtocol.Resp3, 1)]
    [Arguments(RespProtocol.Resp2, 2)]
    [Arguments(RespProtocol.Resp3, 2)]
    public async Task ExpiryTime_PreservesOrderPrecisionAndSentinels(RespProtocol protocol, int mode)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("typed-hash:");
        var key = $"hash-expiry-time:{Guid.NewGuid():N}";
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() * 1000 + 123);
        await client.Hashes.SetAsync(key, ("expiring", "value"), ("persistent", "value"));
        await client.Hashes.ExpireAsync(key, RespireExpiry.At(expires), "expiring");
        string[] fields = ["persistent", "expiring", "missing", "expiring"];

        var defaultPrecision = await ExecuteAsync(client, mode,
            () => client.Hashes.ExpiryTimeAsync(key, fields), h => h.ExpiryTime(key, fields));
        defaultPrecision[1].UnixTimeMilliseconds.Should().Be(expires.ToUnixTimeMilliseconds());
        defaultPrecision[1].GetExpiresAt().Should().Be(expires);
        foreach (var precision in new[] { ExpiryTimePrecision.Milliseconds, ExpiryTimePrecision.Seconds })
        {
            var result = await ExecuteAsync(client, mode,
                () => client.Hashes.ExpiryTimeAsync(key, precision, fields), h => h.ExpiryTime(key, precision, fields));
            result.Should().HaveCount(4);
            result[0].Exists.Should().BeTrue();
            result[0].HasExpiry.Should().BeFalse();
            result[0].UnixTimeMilliseconds.Should().BeNull();
            result[1].Exists.Should().BeTrue();
            result[1].HasExpiry.Should().BeTrue();
            result[1].UnixTimeMilliseconds.Should().Be(precision == ExpiryTimePrecision.Milliseconds
                ? expires.ToUnixTimeMilliseconds() : (expires.ToUnixTimeSeconds() + 1) * 1000);
            result[2].Exists.Should().BeFalse();
            result[2].HasExpiry.Should().BeFalse();
            result[2].UnixTimeMilliseconds.Should().BeNull();
            result[3].UnixTimeMilliseconds.Should().Be(result[1].UnixTimeMilliseconds);
            var missing = await ExecuteAsync(client, mode,
                () => client.Hashes.ExpiryTimeAsync(key + ":missing", precision, fields),
                h => h.ExpiryTime(key + ":missing", precision, fields));
            missing.Should().HaveCount(fields.Length).And.OnlyContain(e => !e.Exists && !e.HasExpiry);
        }
    }

    [Test]
    [Arguments(RespProtocol.Resp2, 0)]
    [Arguments(RespProtocol.Resp3, 0)]
    [Arguments(RespProtocol.Resp2, 1)]
    [Arguments(RespProtocol.Resp3, 1)]
    [Arguments(RespProtocol.Resp2, 2)]
    [Arguments(RespProtocol.Resp3, 2)]
    public async Task WrongType_PropagatesServerErrors(RespProtocol protocol, int mode)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("typed-hash:");
        var key = $"hash-wrongtype:{Guid.NewGuid():N}";
        await client.SetAsync(key, "string");
        await AssertWrongType(() => ExecuteAsync(client, mode,
            () => client.Hashes.RandomFieldAsync(key), h => h.RandomField(key)));
        await AssertWrongType(() => ExecuteAsync(client, mode,
            () => client.Hashes.RandomFieldsAsync(key, 1), h => h.RandomFields(key, 1)));
        await AssertWrongType(() => ExecuteAsync(client, mode,
            () => client.Hashes.RandomFieldsWithValuesAsync(key, 1), h => h.RandomFieldsWithValues(key, 1)));
        await AssertWrongType(() => ExecuteAsync(client, mode,
            () => client.Hashes.LengthAsync(key, "f"), h => h.Length(key, "f")));
        foreach (var precision in new[] { ExpiryTimePrecision.Milliseconds, ExpiryTimePrecision.Seconds })
        {
            await AssertWrongType(() => ExecuteAsync(client, mode,
                () => client.Hashes.ExpiryTimeAsync(key, precision, "f"), h => h.ExpiryTime(key, precision, "f")));
        }
    }

    private ValueTask<RespireClient> ConnectAsync(RespProtocol protocol)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
            Protocol = protocol,
            Connections = 1,
        });

    private static async Task AssertWrongType<T>(Func<Task<T>> action)
    {
        Func<Task> run = async () => { _ = await action(); };
        await run.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
    }

    private static async Task<T> ExecuteAsync<T>(IRespireClient client, int mode,
        Func<ValueTask<T>> immediate, Func<IBatchHashCommands, RespirePending<T>> enqueue)
    {
        if (mode == 0)
        {
            return await immediate();
        }
        if (mode == 1)
        {
            using var batch = client.CreateBatch();
            var pending = enqueue(batch.Hashes);
            await batch.TryExecuteAsync();
            return await pending;
        }
        await using var transaction = client.CreateTransaction();
        var transactionPending = enqueue(transaction.Hashes);
        await transaction.CommitAsync();
        return await transactionPending;
    }
}
