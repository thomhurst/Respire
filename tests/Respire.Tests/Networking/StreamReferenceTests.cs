using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamReferenceTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task PoliciesAndOwnedOutcomesMatchAcrossSurfaces(int surface)
    {
        byte[] outcomes = "*3\r\n:-1\r\n:1\r\n:2\r\n"u8.ToArray();
        byte[][] replies = Enumerable.Range(0, 3).SelectMany(_ => new[]
            { outcomes, outcomes, "$3\r\n9-0\r\n"u8.ToArray(), ":2\r\n"u8.ToArray() }).ToArray();
        if (surface == 2)
        {
            var executed = Encoding.ASCII.GetBytes($"*{replies.Length}\r\n").Concat(replies.SelectMany(x => x)).ToArray();
            replies = [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), replies.Length), executed];
        }
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = surface == 1 ? view.CreateBatch() : null;
        await using var transaction = surface == 2 ? view.CreateTransaction() : null;
        IRespireCommandQueue? queue = transaction ?? (IRespireCommandQueue?)batch;
        var results = new List<RespireStreamDeletionResult[]>();
        var pending = new List<RespirePending<RespireStreamDeletionResult[]>>();
        byte[] key = "events"u8.ToArray();
        RespireStreamId[] ids = ["1-0", "2-0", "3-0"];
        foreach (var policy in Enum.GetValues<StreamReferencePolicy>())
        {
            var add = new StreamAddOptions { Id = "9-0", MaxLength = 0, ApproximateTrim = false, ReferencePolicy = policy };
            var trim = new StreamTrimOptions { MinId = "9-0", Approximate = true, Limit = 0, ReferencePolicy = policy };
            if (queue is null)
            {
                results.Add(await view.Streams.RemoveAsync(key, policy, ids));
                results.Add(await view.Streams.AcknowledgeAndRemoveAsync(key, "workers", policy, ids));
                await Assert.That(await view.Streams.AddAsync(key, add, ("field", "value"))).IsEqualTo((RespireStreamId?)"9-0");
                await Assert.That(await view.Streams.TrimAsync(key, trim)).IsEqualTo(2);
            }
            else
            {
                pending.Add(queue.Streams.Remove(key, policy, ids));
                pending.Add(queue.Streams.AcknowledgeAndRemove(key, "workers", policy, ids));
                _ = queue.Streams.Add(key, add, ("field", "value"));
                _ = queue.Streams.Trim(key, trim);
            }
        }
        Array.Fill(key, (byte)'x');
        Array.Fill(ids, (RespireStreamId)"99-0");
        if (transaction is not null) await transaction.CommitAsync();
        else if (batch is not null) await batch.ExecuteAsync();
        results.AddRange(pending.Select(x => x.Result));
        foreach (var result in results)
            await Assert.That(result).IsEquivalentTo(new[] { RespireStreamDeletionResult.NotFound,
                RespireStreamDeletionResult.Deleted, RespireStreamDeletionResult.Retained }, CollectionOrdering.Matching);
        var expected = new[] { "KEEPREF", "DELREF", "ACKED" }.SelectMany(policy => new[]
        {
            $"XDELEX tenant:events {policy} IDS 3 1-0 2-0 3-0",
            $"XACKDEL tenant:events workers {policy} IDS 3 1-0 2-0 3-0",
            $"XADD tenant:events {policy} MAXLEN 0 9-0 field value",
            $"XTRIM tenant:events {policy} MINID ~ 9-0 LIMIT 0",
        });
        await Assert.That(server.ReceivedCommands.Where(x => x is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidArgumentsFailBeforeIoOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions { Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)] });
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        RespireStreamId[][] invalid = [[], ["$"], ["-"], ["+"], ["1-*"], ["1-"], ["18446744073709551616-0"]];
        foreach (var ids in invalid)
        {
            await Assert.That(async () => await client.Streams.RemoveAsync("events", StreamReferencePolicy.Acknowledged, ids)).Throws<ArgumentException>();
            await Assert.That(async () => await client.Streams.AcknowledgeAndRemoveAsync("events", "g", StreamReferencePolicy.Acknowledged, ids)).Throws<ArgumentException>();
            foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
            {
                await Assert.That(() => queue.Streams.Remove("events", StreamReferencePolicy.Acknowledged, ids)).Throws<ArgumentException>();
                await Assert.That(() => queue.Streams.AcknowledgeAndRemove("events", "g", StreamReferencePolicy.Acknowledged, ids)).Throws<ArgumentException>();
            }
        }
        var unknown = (StreamReferencePolicy)99;
        await Assert.That(async () => await client.Streams.RemoveAsync("events", unknown, "1-0")).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Streams.AcknowledgeAndRemoveAsync("events", null!, StreamReferencePolicy.KeepReferences, "1-0"))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Streams.TrimAsync("events", new() { MaxLength = 0, ReferencePolicy = unknown })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => batch.Streams.Add("events", new StreamAddOptions { ReferencePolicy = unknown }, ("f", "v")))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => transaction.Streams.Trim("events", new() { MaxLength = 0, ReferencePolicy = unknown }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
        var options = new StreamAddOptions { ReferencePolicy = StreamReferencePolicy.KeepReferences };
        await Assert.That(options == default).IsFalse();
        await Assert.That(options == options with { ReferencePolicy = StreamReferencePolicy.DeleteReferences }).IsFalse();
    }

    [Test]
    [Arguments(":1\r\n")]
    [Arguments("*1\r\n:0\r\n")]
    [Arguments("*1\r\n+OK\r\n")]
    public async Task MalformedOutcomesFailWithoutPoisoningNextReply(string response)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(response), "*1\r\n:1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Streams.RemoveAsync("events", StreamReferencePolicy.KeepReferences, "1-0"))
            .Throws<RespireProtocolException>();
        await Assert.That(await client.Streams.RemoveAsync("events", StreamReferencePolicy.KeepReferences, "1-0"))
            .IsEquivalentTo(new[] { RespireStreamDeletionResult.Deleted });
    }

    [Test]
    public async Task ServerErrorsAreNotReplayed()
    {
        await using var server = new FakeRespServer("-ERR unknown command\r\n"u8.ToArray(), "-ERR syntax error\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Streams.RemoveAsync("events", StreamReferencePolicy.KeepReferences, "1-0")).Throws<RespireServerException>();
        await Assert.That(async () => await client.Streams.AcknowledgeAndRemoveAsync("events", "g", StreamReferencePolicy.DeleteReferences, "1-0")).Throws<RespireServerException>();
        await Assert.That(async () => await client.Streams.AddAsync("events", new StreamAddOptions { ReferencePolicy = StreamReferencePolicy.Acknowledged }, ("f", "v"))).Throws<RespireServerException>();
        await Assert.That(async () => await client.Streams.TrimAsync("events", new() { MaxLength = 0, ReferencePolicy = StreamReferencePolicy.Acknowledged })).Throws<RespireServerException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(4);
    }

    [Test]
    public async Task CancellationBeforeDispatchDoesNotSendWrites()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions { Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)] });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Streams.RemoveAsync("events", StreamReferencePolicy.DeleteReferences,
            ["1-0"], cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Streams.AcknowledgeAndRemoveAsync("events", "g", StreamReferencePolicy.DeleteReferences,
            ["1-0"], cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ClusterRoutesRemovalToPrefixedKeyOwner()
    {
        await using var owner = new FakeRespServer("*1\r\n:1\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("{tenant}:events");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
            { Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)] });
        var view = client.WithKeyPrefix("{tenant}:");
        await view.Streams.RemoveAsync("events", StreamReferencePolicy.KeepReferences, "1-0");
        await view.Streams.AcknowledgeAndRemoveAsync("events", "group", StreamReferencePolicy.DeleteReferences, "1-0");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(new[] { "CLUSTER SLOTS" });
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(new[]
        {
            "XDELEX {tenant}:events KEEPREF IDS 1 1-0", "XACKDEL {tenant}:events group DELREF IDS 1 1-0",
        }, CollectionOrdering.Matching);
    }
}
