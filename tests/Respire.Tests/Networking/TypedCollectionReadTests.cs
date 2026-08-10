using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TypedCollectionReadTests
{
    [Test]
    public async Task TypedCollectionReads_DeserializeValuesAndPreserveMissingEntries()
    {
        const string first = "{\"Name\":\"one\"}";
        const string second = "{\"Name\":\"two\"}";
        await using var server = new FakeRespServer(
            ArrayReply(first, null, second),
            ArrayReply(first, second),
            ArrayReply(first, second),
            ArrayReply("first", first, "second", second));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var strings = await client.Strings.GetManyAsync<Payload>("a", "missing", "b");
        var list = await client.Lists.RangeAsync<Payload>("list");
        var set = await client.Sets.MembersAsync<Payload>("set");
        var hash = await client.Hashes.GetAllAsync<Payload>("hash");

        await Assert.That(strings).IsEquivalentTo(new Payload?[] { new("one"), null, new("two") });
        await Assert.That(list).IsEquivalentTo(new[] { new Payload("one"), new Payload("two") });
        await Assert.That(set).IsEquivalentTo(new[] { new Payload("one"), new Payload("two") });
        await Assert.That(hash).IsEquivalentTo(new Dictionary<string, Payload>
        {
            ["first"] = new("one"),
            ["second"] = new("two"),
        });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "MGET a missing b",
            "LRANGE list 0 -1",
            "SMEMBERS set",
            "HGETALL hash",
        });
    }

    [Test]
    public async Task DeferredTypedCollectionReads_DeserializeValuesAndPreserveMissingEntries()
    {
        const string first = "{\"Name\":\"one\"}";
        const string second = "{\"Name\":\"two\"}";
        await using var server = new FakeRespServer(
            ArrayReply(first, null, second),
            ArrayReply(first, second),
            ArrayReply(first, second),
            ArrayReply("first", first, "second", second));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();

        var strings = batch.Strings.GetMany<Payload>("a", "missing", "b");
        var list = batch.Lists.Range<Payload>("list");
        var set = batch.Sets.Members<Payload>("set");
        var hash = batch.Hashes.GetAll<Payload>("hash");

        await batch.ExecuteAsync();

        await Assert.That(strings.Result).IsEquivalentTo(
            new Payload?[] { new("one"), null, new("two") });
        await Assert.That(list.Result).IsEquivalentTo(
            new[] { new Payload("one"), new Payload("two") });
        await Assert.That(set.Result).IsEquivalentTo(
            new[] { new Payload("one"), new Payload("two") });
        await Assert.That(hash.Result).IsEquivalentTo(new Dictionary<string, Payload>
        {
            ["first"] = new("one"),
            ["second"] = new("two"),
        });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "MGET a missing b",
            "LRANGE list 0 -1",
            "SMEMBERS set",
            "HGETALL hash",
        });
    }

    private static byte[] ArrayReply(params string?[] values)
    {
        var reply = new StringBuilder().Append('*').Append(values.Length).Append("\r\n");
        foreach (var value in values)
        {
            if (value is null)
            {
                reply.Append("$-1\r\n");
                continue;
            }

            reply.Append('$').Append(Encoding.UTF8.GetByteCount(value)).Append("\r\n")
                .Append(value).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(reply.ToString());
    }

    private sealed record Payload(string Name);
}
