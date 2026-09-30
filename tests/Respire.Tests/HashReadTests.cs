using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class HashReadTests
{
    [Test]
    [Arguments(-2L, false)]
    [Arguments(-1L, false)]
    [Arguments(0L, true)]
    [Arguments(253402300799999L, true)]
    [Arguments(253402300800000L, false)]
    [Arguments(long.MaxValue, false)]
    public async Task ExpiryTime_TryConversionPreservesStatesAndTimestamp(long timestamp, bool convertible)
    {
        var expiry = RespireExpiryTime.FromRedis(timestamp, ExpiryTimePrecision.Milliseconds);
        await Assert.That(expiry.TryGetExpiresAt(out var instant)).IsEqualTo(convertible);
        if (convertible)
        {
            await Assert.That(instant.ToUnixTimeMilliseconds()).IsEqualTo(timestamp);
        }
        else
        {
            await Assert.That(instant).IsEqualTo(default(DateTimeOffset));
        }
        await Assert.That(expiry.UnixTimeMilliseconds).IsEqualTo(timestamp < 0 ? null : (long?)timestamp);
    }

    [Test]
    public async Task ExpiryTime_VariadicOverloadsSelectPrecisionAndPreserveFields()
    {
        await using var server = new FakeRespServer(
            "*2\r\n:1000\r\n:-1\r\n"u8.ToArray(), "*1\r\n:1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var milliseconds = await client.Hashes.ExpiryTimeAsync("hash", "a", "b");
        var seconds = await client.Hashes.ExpiryTimeAsync("hash", ExpiryTimePrecision.Seconds, "a");

        await Assert.That(milliseconds[0].UnixTimeMilliseconds).IsEqualTo(1000);
        await Assert.That(seconds[0].UnixTimeMilliseconds).IsEqualTo(1000);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "HPEXPIRETIME hash FIELDS 2 a b", "HEXPIRETIME hash FIELDS 1 a",
        ]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RandomPairs_PreserveDuplicateFieldsAndDetachStrings(bool nested)
    {
        KeyValuePair<string, string>[] result;
        using (var reply = nested
            ? RespValue.Array(RespValue.Array(RespValue.BulkString("a"), RespValue.BulkString("é")),
                RespValue.Array(RespValue.BulkString("a"), RespValue.BulkString("é")))
            : RespValue.Array(RespValue.BulkString("a"), RespValue.BulkString("é"),
                RespValue.BulkString("a"), RespValue.BulkString("é")))
        {
            result = HashCommands.ParseRandomPairs(in reply);
        }
        await Assert.That(result).IsEquivalentTo(new[]
        {
            new KeyValuePair<string, string>("a", "é"),
            new KeyValuePair<string, string>("a", "é"),
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RandomPairs_RejectIncompletePairs(bool nested)
    {
        using var reply = nested
            ? RespValue.Array(RespValue.Array(RespValue.BulkString("field")))
            : RespValue.Array(RespValue.BulkString("field"));
        await Assert.That(() => HashCommands.ParseRandomPairs(in reply)).Throws<RespireProtocolException>();
    }

    [Test]
    public async Task ExpiryTime_PreservesLargeTimestampsWithoutDateTimeOverflow()
    {
        const long timestamp = 281474976710000;
        var milliseconds = RespireExpiryTime.FromRedis(timestamp, ExpiryTimePrecision.Milliseconds);
        var seconds = RespireExpiryTime.FromRedis(timestamp / 1000, ExpiryTimePrecision.Seconds);
        await Assert.That(milliseconds.UnixTimeMilliseconds).IsEqualTo(timestamp);
        await Assert.That(seconds.UnixTimeMilliseconds).IsEqualTo(timestamp);
        await Assert.That(milliseconds.Exists && milliseconds.HasExpiry).IsTrue();
        await Assert.That(default(RespireExpiryTime).Exists).IsFalse();
        await Assert.That(() => milliseconds.GetExpiresAt()).Throws<ArgumentOutOfRangeException>();
        await Assert.That(default(RespireExpiryTime).GetExpiresAt()).IsNull();
    }

    [Test]
    public async Task ExpiryTime_RejectsOverflowingSecondsAsMalformedReplies()
    {
        const long largestSeconds = long.MaxValue / 1000;
        await Assert.That(RespireExpiryTime.FromRedis(largestSeconds, ExpiryTimePrecision.Seconds).UnixTimeMilliseconds)
            .IsEqualTo(largestSeconds * 1000);
        await Assert.That(() => RespireExpiryTime.FromRedis(largestSeconds + 1, ExpiryTimePrecision.Seconds))
            .Throws<RespireProtocolException>();
        await Assert.That(() => RespireExpiryTime.FromRedis(long.MaxValue, ExpiryTimePrecision.Seconds))
            .Throws<RespireProtocolException>();
    }
    [Test]
    public async Task ExpiryTime_RejectsUnexpectedNegativeSentinels()
    {
        await Assert.That(() => RespireExpiryTime.FromRedis(-3, ExpiryTimePrecision.Milliseconds))
            .Throws<RespireProtocolException>();
    }

    [Test]
    public async Task ExpiryTime_RejectsEmptyFieldsAndInvalidPrecisionBeforeEnqueueing()
    {
        await using var client = RespireClient.Create("localhost:1");
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(async () => await client.Hashes.ExpiryTimeAsync("hash")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Hashes.ExpiryTimeAsync("hash", [], CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Hashes.ExpiryTimeAsync("hash", ExpiryTimePrecision.Seconds)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Hashes.ExpiryTimeAsync("hash", (ExpiryTimePrecision)99, "field"))
            .Throws<ArgumentOutOfRangeException>();
        foreach (var hashes in new[] { batch.Hashes, transaction.Hashes })
        {
            await Assert.That(() => hashes.ExpiryTime("hash")).Throws<ArgumentException>();
            await Assert.That(() => hashes.ExpiryTime("hash", ExpiryTimePrecision.Seconds)).Throws<ArgumentException>();
            await Assert.That(() => hashes.ExpiryTime("hash", (ExpiryTimePrecision)99, "field"))
                .Throws<ArgumentOutOfRangeException>();
        }
        await Assert.That(batch.Count).IsEqualTo(0);
    }
}
