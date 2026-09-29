using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SetPopManyTests
{
    [Test]
    public async Task Immediate_DefaultArgumentsBindScalarAndCountCallsSeparately()
    {
        await using var server = new FakeRespServer(
            "$-1\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(),
            "*0\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(), "*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        string? member = await client.Sets.PopAsync("missing", default);
        SortedSetEntry? entry = await client.SortedSets.PopAsync("missing", default);
        SortedSetEntry<int>? typed = await client.SortedSets.PopAsync<int>("missing", default);
        string[] members = await client.Sets.PopManyAsync("missing", default);
        SortedSetEntry[] entries = await client.SortedSets.PopManyAsync("missing", default);
        SortedSetEntry<int>[] typedEntries = await client.SortedSets.PopManyAsync<int>("missing", default);

        await Assert.That(member).IsNull();
        await Assert.That(entry).IsNull();
        await Assert.That(typed).IsNull();
        await Assert.That(members).IsEmpty();
        await Assert.That(entries).IsEmpty();
        await Assert.That(typedEntries).IsEmpty();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SPOP missing", "ZPOPMIN missing", "ZPOPMIN missing",
            "SPOP missing 0", "ZPOPMIN missing 0", "ZPOPMIN missing 0",
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Deferred_CountPopsReturnEmptyArraysForMissingKeys(bool transactional)
    {
        byte[][] results =
        [
            "$-1\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(),
            "*0\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(),
        ];
        var replies = transactional
            ? new[] { FakeRespServer.OkReply }
                .Concat(Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), results.Length))
                .Append("*6\r\n"u8.ToArray().Concat(results.SelectMany(result => result)).ToArray())
                .ToArray()
            : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var transaction = transactional ? client.CreateTransaction() : null;
        var sets = transaction?.Sets ?? batch.Sets;
        var sortedSets = transaction?.SortedSets ?? batch.SortedSets;

        var member = sets.Pop("missing");
        var entry = sortedSets.Pop("missing", default);
        var typed = sortedSets.Pop<int>("missing", default);
        var members = sets.PopMany("missing", 2);
        var entries = sortedSets.PopMany("missing", 2, descending: true);
        var typedEntries = sortedSets.PopMany<int>("missing", 2);
        if (transaction is not null)
            await transaction.CommitAsync();
        else
            await batch.ExecuteAsync();

        await Assert.That(member.Result).IsNull();
        await Assert.That(entry.Result).IsNull();
        await Assert.That(typed.Result).IsNull();
        await Assert.That(members.Result).IsEmpty();
        await Assert.That(entries.Result).IsEmpty();
        await Assert.That(typedEntries.Result).IsEmpty();
        var commands = server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC");
        await Assert.That(commands).IsEquivalentTo(new[]
        {
            "SPOP missing", "ZPOPMIN missing", "ZPOPMIN missing",
            "SPOP missing 2", "ZPOPMAX missing 2", "ZPOPMIN missing 2",
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Immediate_CountPopsForwardCancellation(int variant)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ =>
            {
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task pending = variant switch
        {
            0 => client.Sets.PopManyAsync("members", 2, cancellation.Token).AsTask(),
            1 => client.SortedSets.PopManyAsync("scores", 2, cancellationToken: cancellation.Token).AsTask(),
            _ => client.SortedSets.PopManyAsync<int>("scores", 2, cancellationToken: cancellation.Token).AsTask(),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }
}
