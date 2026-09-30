using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamTrimmingTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task TrimOptionsHaveIdenticalWireEncodingAcrossSurfaces(int surface)
    {
        StreamTrimOptions[] options =
        [
            new() { MaxLength = 2 }, new() { MinId = "2-0" }, new() { MinId = "2" },
            new() { MaxLength = 2, Approximate = true }, new() { MinId = "2-0", Approximate = true },
            new() { MaxLength = 2, Approximate = true, Limit = 0 },
            new() { MinId = "2-0", Approximate = true, Limit = 10 },
        ];
        string[] clauses = ["MAXLEN 2", "MINID 2-0", "MINID 2", "MAXLEN ~ 2", "MINID ~ 2-0",
            "MAXLEN ~ 2 LIMIT 0", "MINID ~ 2-0 LIMIT 10"];
        byte[][] replies = options.SelectMany(_ => new[] { "$3\r\n1-0\r\n"u8.ToArray(), ":2\r\n"u8.ToArray() }).ToArray();
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
        var pending = new List<(RespirePending<RespireStreamId?> Added, RespirePending<long> Trimmed)>();
        byte[] key = "events"u8.ToArray();
        foreach (var trim in options)
        {
            var add = new StreamAddOptions { Id = "1-0", MaxLength = trim.MaxLength, MinId = trim.MinId,
                ApproximateTrim = trim.Approximate, Limit = trim.Limit };
            if (queue is null)
            {
                await Assert.That(await view.Streams.AddAsync(key, add, ("field", "value")))
                    .IsEqualTo((RespireStreamId?)new RespireStreamId("1-0"));
                await Assert.That(await view.Streams.TrimAsync(key, trim)).IsEqualTo(2);
            }
            else pending.Add((queue.Streams.Add(key, add, ("field", "value")), queue.Streams.Trim(key, trim)));
        }
        Array.Fill(key, (byte)'x');
        if (transaction is not null) await transaction.CommitAsync();
        else if (batch is not null) await batch.ExecuteAsync();
        foreach (var result in pending)
        {
            await Assert.That(result.Added.Result).IsEqualTo((RespireStreamId?)new RespireStreamId("1-0"));
            await Assert.That(result.Trimmed.Result).IsEqualTo(2);
        }
        var expected = clauses.SelectMany(clause => new[]
        {
            $"XADD tenant:events {clause} 1-0 field value", $"XTRIM tenant:events {clause}",
        });
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidTrimmingFailsBeforeIoOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions { Connections = 1, Endpoints = [new("127.0.0.1", server.Port)] });
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        StreamTrimOptions[] invalid =
        [
            new() { MaxLength = 1, MinId = "1-0" }, new() { MaxLength = -1 },
            new() { MinId = "1-0", Limit = 1 }, new() { Limit = 1, Approximate = true },
            new() { MaxLength = 1, Approximate = true, Limit = -1 },
            new() { MinId = "$" }, new() { MinId = "-" }, new() { MinId = "+" },
            new() { MinId = "1-*" }, new() { MinId = "18446744073709551616-0" },
            new() { MinId = "-1" }, new() { MinId = "1-" },
        ];
        foreach (var trim in invalid)
        {
            var add = new StreamAddOptions { MaxLength = trim.MaxLength, MinId = trim.MinId,
                ApproximateTrim = trim.Approximate, Limit = trim.Limit };
            var trimError = await Assert.That(async () => await client.Streams.TrimAsync("events", trim))
                .Throws<ArgumentException>();
            var addError = await Assert.That(async () => await client.Streams.AddAsync("events", add, ("field", "value")))
                .Throws<ArgumentException>();
            await Assert.That(trimError!.ParamName).IsEqualTo("options");
            await Assert.That(addError!.ParamName).IsEqualTo("options");
            foreach (var stream in new[] { batch.Streams, transaction.Streams })
            {
                await Assert.That(() => stream.Trim("events", trim)).Throws<ArgumentException>();
                await Assert.That(() => stream.Add("events", add, ("field", "value"))).Throws<ArgumentException>();
            }
        }
        await Assert.That(async () => await client.Streams.TrimAsync("events", default)).Throws<ArgumentException>();
        await Assert.That(() => batch.Streams.Trim("events", default)).Throws<ArgumentException>();
        await Assert.That(() => transaction.Streams.Trim("events", default)).Throws<ArgumentException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task AddLimitWithoutThresholdRejectsDefaultApproximationBeforeIoOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        var options = new StreamAddOptions { Limit = 1 };
        var error = await Assert.That(async () => await client.Streams.AddAsync("events", options, ("field", "value")))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(error!.ParamName).IsEqualTo("options");
        await Assert.That(error.Message).Contains("LIMIT requires a trimming threshold");
        await Assert.That(() => batch.Streams.Add("events", options, ("field", "value"))).ThrowsExactly<ArgumentException>();
        await Assert.That(() => transaction.Streams.Add("events", options, ("field", "value"))).ThrowsExactly<ArgumentException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task AddOptionEqualityIncludesBothNewFields()
    {
        var original = new StreamAddOptions { MinId = "1-0", Limit = 10 };
        await Assert.That(original == original with { MinId = "2-0" }).IsFalse();
        await Assert.That(original == original with { Limit = 11 }).IsFalse();
        await Assert.That(original == original with { ApproximateTrim = true, CreateStream = true }).IsTrue();
        await Assert.That(default(StreamAddOptions).ApproximateTrim).IsTrue();
        await Assert.That(default(StreamTrimOptions).Approximate).IsFalse();
    }
}
