using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DeferredTypedFacetTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedCommandsMatchImmediateSerializationAndReplies(bool transaction)
    {
        var person = new Person("Ada");
        var json = Bulk("{\"Name\":\"Ada\"}");
        var one = ":1\r\n"u8.ToArray();
        byte[][] replies = [one, one, one, json, one, one, json, json];
        await using var server = new FakeRespServer([.. replies, .. DeferredReplies(transaction, replies)]);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.Hashes.SetAsync("hash", "person", person);
        await client.Hashes.SetAsync("hash", "new", person, SetWhen.NotExists);
        await client.Hashes.SetAsync("hash", "existing", person, SetWhen.Exists);
        var immediateGet = await client.Hashes.TryGetAsync<Person>("hash", "person");
        await client.Sets.ContainsAsync("set", true);
        await client.SortedSets.AddAsync("sorted", false, 2);
        var immediateLeft = await client.Lists.LeftPopAsync<Person>("list");
        var immediateRight = await client.Lists.RightPopAsync<Person>("list");
        var expected = server.ReceivedCommands.ToArray();

        await ExecuteAsync(client, transaction, async (queue, execute) =>
        {
            var stored = queue.Hashes.Set("hash", "person", person);
            var inserted = queue.Hashes.Set("hash", "new", person, SetWhen.NotExists);
            var updated = queue.Hashes.Set("hash", "existing", person, SetWhen.Exists);
            var found = queue.Hashes.TryGet<Person>("hash", "person");
            var contains = queue.Sets.Contains("set", true);
            var added = queue.SortedSets.Add("sorted", false, 2);
            var left = queue.Lists.LeftPop<Person>("list");
            var right = queue.Lists.RightPop<Person>("list");
            await execute();

            await Assert.That(stored.Result && inserted.Result && updated.Result).IsTrue();
            await Assert.That(found.Result).IsEqualTo(immediateGet);
            await Assert.That(found.Result.Value).IsEqualTo(person);
            await Assert.That(contains.Result && added.Result).IsTrue();
            await Assert.That(left.Result).IsEqualTo(immediateLeft);
            await Assert.That(right.Result).IsEqualTo(immediateRight);
        });

        var actual = server.ReceivedCommands.Skip(replies.Length)
            .Where(command => command is not "MULTI" and not "EXEC");
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(expected[4]).IsEqualTo("SISMEMBER set 1");
        await Assert.That(expected[5]).IsEqualTo("ZADD sorted 2 0");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingValuesAndStoredDefaultsRemainDistinct(bool transaction)
    {
        var missing = "$-1\r\n"u8.ToArray();
        byte[][] replies = [missing, Bulk("0"), missing, missing];
        await using var server = new FakeRespServer(DeferredReplies(transaction, replies));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await ExecuteAsync(client, transaction, async (queue, execute) =>
        {
            var absent = queue.Hashes.TryGet<int>("hash", "absent");
            var zero = queue.Hashes.TryGet<int>("hash", "zero");
            var left = queue.Lists.LeftPop<Person>("empty");
            var right = queue.Lists.RightPop<int>("empty");
            await execute();

            await Assert.That(absent.Result.Found).IsFalse();
            await Assert.That(zero.Result.Found).IsTrue();
            await Assert.That(zero.Result.Value).IsEqualTo(0);
            await Assert.That(left.Result).IsNull();
            await Assert.That(right.Result).IsEqualTo(0);
        });
    }

    private static async Task ExecuteAsync(RespireClient client, bool transaction,
        Func<IRespireCommandQueue, Func<Task>, Task> test)
    {
        if (transaction)
        {
            await using var queue = client.CreateTransaction();
            await test(queue, async () => await queue.CommitAsync());
        }
        else
        {
            using var queue = client.CreateBatch();
            await test(queue, async () => (await queue.ExecuteAsync()).ThrowIfAnyFailed());
        }
    }

    private static byte[][] DeferredReplies(bool transaction, byte[][] replies)
        => transaction
            ? [FakeRespServer.OkReply,
               .. replies.Select(_ => "+QUEUED\r\n"u8.ToArray()),
               [.. Encoding.ASCII.GetBytes($"*{replies.Length}\r\n"), .. replies.SelectMany(reply => reply)]]
            : replies;

    private static byte[] Bulk(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [.. Encoding.ASCII.GetBytes($"${bytes.Length}\r\n"), .. bytes, .. "\r\n"u8];
    }

    public sealed record Person(string Name);
}
