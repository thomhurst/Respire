using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ModernRedisTypedCommandTests
{
    [Test]
    public async Task MSetExCommand_RoutesByFirstKeyAfterCount()
    {
        var command = new MSetExCommand(
            RespireCommands.String.MSETEX.Verb,
            [2, "first-key", "one", "second-key", "two", "PX", 1000]);

        await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
        await Assert.That(slot).IsEqualTo(Respire.Internal.ClusterHash.GetSlot("first-key"));
    }

    [Test]
    public async Task HashFieldExpiryCommands_WriteExpectedFramesAndParseReplies()
    {
        await using var server = new FakeRespServer(
            "*3\r\n:1234\r\n:-1\r\n:-2\r\n"u8.ToArray(),
            "*4\r\n:1\r\n:0\r\n:-2\r\n:2\r\n"u8.ToArray(),
            "*1\r\n:1\r\n"u8.ToArray(),
            "*3\r\n:1\r\n:-1\r\n:-2\r\n"u8.ToArray(),
            "*2\r\n$5\r\nhello\r\n$-1\r\n"u8.ToArray(),
            "*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray(),
            "*1\r\n$5\r\nvalue\r\n"u8.ToArray(),
            "*1\r\n$9\r\npersisted\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var expiries = await client.Hashes.ExpiryAsync("hash", "a", "b", "c");
        var expireResults = await client.Hashes.ExpireAsync(
            "hash", TimeSpan.FromMilliseconds(2500), HashFieldExpireWhen.GreaterThan, "a", "b", "c", "d");
        var expireAtResults = await client.Hashes.ExpireAtAsync(
            "hash", DateTimeOffset.FromUnixTimeMilliseconds(123456789), "a");
        var persistResults = await client.Hashes.PersistAsync("hash", "a", "b", "c");
        var deleted = await client.Hashes.GetDeleteAsync("hash", "a", "missing");
        var getExpire = await client.Hashes.GetExpireAsync("hash", TimeSpan.FromSeconds(5), "a", "b");
        var getExpireAt = await client.Hashes.GetExpireAtAsync(
            "hash", DateTimeOffset.FromUnixTimeMilliseconds(987654321), "c");
        var getPersist = await client.Hashes.GetPersistAsync("hash", "a");
        var setExpire = await client.Hashes.SetExpireAsync(
            "hash", TimeSpan.FromSeconds(2), SetWhen.NotExists, ("a", "one"), ("b", "two"));
        var setExpireAt = await client.Hashes.SetExpireAtAsync(
            "hash", DateTimeOffset.FromUnixTimeMilliseconds(987654321), SetWhen.Exists, ("a", "next"));

        await Assert.That(expiries[0].FieldExists).IsTrue();
        await Assert.That(expiries[0].HasExpiry).IsTrue();
        await Assert.That(expiries[0].TimeToLive).IsEqualTo(TimeSpan.FromMilliseconds(1234));
        await Assert.That(expiries[1].FieldExists).IsTrue();
        await Assert.That(expiries[1].HasExpiry).IsFalse();
        await Assert.That(expiries[2].FieldExists).IsFalse();
        await Assert.That(expireResults).IsEquivalentTo(
            new[]
            {
                HashFieldExpiryResult.Applied,
                HashFieldExpiryResult.ConditionNotMet,
                HashFieldExpiryResult.NoSuchField,
                HashFieldExpiryResult.Deleted,
            },
            CollectionOrdering.Matching);
        await Assert.That(expireAtResults).IsEquivalentTo(
            new[] { HashFieldExpiryResult.Applied }, CollectionOrdering.Matching);
        await Assert.That(persistResults).IsEquivalentTo(
            new[]
            {
                HashFieldExpiryResult.Applied,
                HashFieldExpiryResult.NoExpiry,
                HashFieldExpiryResult.NoSuchField,
            },
            CollectionOrdering.Matching);
        await Assert.That(deleted).IsEquivalentTo(new string?[] { "hello", null }, CollectionOrdering.Matching);
        await Assert.That(getExpire).IsEquivalentTo(new string?[] { "a", "b" }, CollectionOrdering.Matching);
        await Assert.That(getExpireAt).IsEquivalentTo(new string?[] { "value" }, CollectionOrdering.Matching);
        await Assert.That(getPersist).IsEquivalentTo(new string?[] { "persisted" }, CollectionOrdering.Matching);
        await Assert.That(setExpire).IsTrue();
        await Assert.That(setExpireAt).IsFalse();

        await AssertCommands(server.ReceivedCommands,
            "HPTTL hash FIELDS 3 a b c",
            "HPEXPIRE hash 2500 GT FIELDS 4 a b c d",
            "HPEXPIREAT hash 123456789 FIELDS 1 a",
            "HPERSIST hash FIELDS 3 a b c",
            "HGETDEL hash FIELDS 2 a missing",
            "HGETEX hash PX 5000 FIELDS 2 a b",
            "HGETEX hash PXAT 987654321 FIELDS 1 c",
            "HGETEX hash PERSIST FIELDS 1 a",
            "HSETEX hash FNX PX 2000 FIELDS 2 a one b two",
            "HSETEX hash FXX PXAT 987654321 FIELDS 1 a next");
    }

    [Test]
    public async Task StringModernCommands_WriteExpectedFramesAndParseReplies()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            "$6\r\nmytext\r\n"u8.ToArray(),
            ":6\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var setExpire = await client.Strings.SetManyAsync(
            TimeSpan.FromSeconds(3), SetWhen.Exists, ("a", "1"), ("b", "2"));
        var setExpireAt = await client.Strings.SetManyAsync(
            RespireTtl.At(DateTimeOffset.FromUnixTimeMilliseconds(1111111111)), pairs: ("c", "3"));
        var keepExpiry = await client.Strings.SetManyAsync(RespireTtl.Keep, SetWhen.NotExists, ("d", "4"));
        var lcs = await client.Strings.LongestCommonSubsequenceAsync("a", "b");
        var lcsLength = await client.Strings.LongestCommonSubsequenceLengthAsync("a", "b");

        await Assert.That(setExpire).IsTrue();
        await Assert.That(setExpireAt).IsTrue();
        await Assert.That(keepExpiry).IsFalse();
        await Assert.That(lcs).IsEqualTo("mytext");
        await Assert.That(lcsLength).IsEqualTo(6);

        await AssertCommands(server.ReceivedCommands,
            "MSETEX 2 a 1 b 2 XX PX 3000",
            "MSETEX 1 c 3 PXAT 1111111111",
            "MSETEX 1 d 4 NX KEEPTTL",
            "LCS a b",
            "LCS a b LEN");
    }

    [Test]
    public async Task ModernCommands_RejectInvalidShapesBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Hashes.ExpiryAsync("hash")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Hashes.ExpireAsync("hash", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Hashes.ExpireAsync(
            "hash", TimeSpan.FromSeconds(1), (HashFieldExpireWhen)42, "field"))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Hashes.SetExpireAsync("hash", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Hashes.SetExpireAsync(
            "hash", TimeSpan.FromSeconds(1), (SetWhen)42, ("field", "value")))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Strings.SetManyAsync(TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Strings.SetManyAsync(RespireTtl.Keep, (SetWhen)42, ("key", "value")))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    private static async Task AssertCommands(IReadOnlyList<string> actual, params string[] expected)
    {
        await Assert.That(actual.Count).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
        }
    }
}
