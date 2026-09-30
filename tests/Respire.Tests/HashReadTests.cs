using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class HashReadTests
{
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
        await Assert.That(() => milliseconds.ExpiresAt).Throws<ArgumentOutOfRangeException>();
        await Assert.That(default(RespireExpiryTime).ExpiresAt).IsNull();
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
