using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Internal;
using Respire.Protocol;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamDeadLetterIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AtomicCompletionPreservesOtherGroupsAndOriginalBytes(int protocol)
    {
        await using var real = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        await using var server = new RespireFakeServer();
        await using var fake = await RespireClient.ConnectAsync(server.CreateOptions()
            with { Protocol = (RespProtocol)protocol });
        foreach (var root in new[] { real, fake })
        {
            var client = root.WithKeyPrefix("{worker}tenant:");
            // Duplicate metadata names and non-UTF8 field names must survive byte-for-byte.
            using (await root.ExecuteAsync("XADD", ["{worker}tenant:s", "1-0",
                new byte[] { 0xff, 0x00 }, new byte[] { 0x00, 0xff, 0x0d, 0x0a },
                "_respire.source_id", "original", "payload", "body"])) { }
            await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
            await client.Streams.CreateGroupAsync("s", "other", RespireStreamId.Beginning);
            await client.Streams.ReadGroupOnceAsync("s", "g", "old");
            await client.Streams.ReadGroupOnceAsync("s", "other", "independent");
            using var claim = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["s"],
                ["g", "current", 0, "0-0", 1]);
            await Assert.That(await CompleteAsync(client, "old", 1)).IsEqualTo(0);
            await Assert.That(await CompleteAsync(client, "current", 1)).IsEqualTo(0);
            // Same-name reclaim changes the attempt token too.
            using var sameName = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["s"],
                ["g", "current", 0, "0-0", 1]);
            await Assert.That(await CompleteAsync(client, "current", 2)).IsEqualTo(0);
            await Assert.That(await CompleteAsync(client, "current", 3)).IsEqualTo(1);
            await Assert.That(await CompleteAsync(client, "current", 3)).IsEqualTo(0);
            await Assert.That((await client.Streams.PendingSummaryAsync("s", "g")).Count).IsEqualTo(0);
            await Assert.That((await client.Streams.PendingSummaryAsync("s", "other")).Count).IsEqualTo(1);
            await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(1);
            await Assert.That(await root.Streams.CountAsync("s")).IsEqualTo(0);
            using var source = await root.ExecuteAsync("XREAD", ["STREAMS", "{worker}tenant:s", "0"]);
            using var target = await root.ExecuteAsync("XREAD", ["STREAMS", "{worker}tenant:dlq", "0"]);
            var targetEntries = target.Type == RespDataType.Map ? target[1] : target[0][1];
            var sourceEntries = source.Type == RespDataType.Map ? source[1] : source[0][1];
            await Assert.That(targetEntries.Count).IsEqualTo(1);
            var fields = targetEntries[0][1];
            await Assert.That(fields[1].AsString()).IsEqualTo("1-0");
            await Assert.That(fields[5].AsString()).IsEqualTo("3");
            await Assert.That(fields[9].AsBytes().Length).IsEqualTo(256);
            for (var i = 0; i < sourceEntries[0][1].Count; i++)
                await Assert.That(fields[i + 10].AsBytes().SequenceEqual(sourceEntries[0][1][i].AsBytes())).IsTrue();
        }
    }

    private static ValueTask<long> CompleteAsync(IRespireClient client, string owner, long attempt)
        => client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.DeadLetter, ["s", "dlq"],
            ["g", owner, "1-0", attempt, "explicit", new string('x', 300)]);

    [Test]
    [Arguments(1024)]
    [Arguments(1025)]
    [Arguments(10000)]
    public async Task WideEntryCompletionHasExplicitLimitWithoutPartialWrites(int fieldCount)
    {
        await using var real = await RespireClient.ConnectAsync(redis.ConnectionString);
        await using var server = new RespireFakeServer();
        await using var fake = await RespireClient.ConnectAsync(server.CreateOptions());
        foreach (var client in new[] { real, fake })
        {
            var fields = Enumerable.Range(0, fieldCount).Select(index => ($"field-{index}", (RespireValue)$"value-{index}")).ToArray();
            await client.Streams.AddAsync("s", new StreamAddOptions { Id = "1-0" }, fields);
            await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
            await client.Streams.ReadGroupOnceAsync("s", "g", "owner");
            if (fieldCount > 1024)
            {
                var error = await Assert.That(async () => await CompleteAsync(client, "owner", 1)).Throws<RespireServerException>();
                await Assert.That(error!.Message).Contains("dead-letter entries support at most 1024 field/value pairs");
                await Assert.That((await client.Streams.PendingSummaryAsync("s", "g")).Count).IsEqualTo(1);
                await Assert.That(await client.Streams.CountAsync("dlq")).IsEqualTo(0);
            }
            else
            {
                await Assert.That(await CompleteAsync(client, "owner", 1)).IsEqualTo(1);
                var entry = (await client.Streams.ReadAsync("dlq")).Single();
                await Assert.That(entry.Fields.Count).IsEqualTo(fieldCount + 5);
                for (var index = 0; index < fieldCount; index++)
                    await Assert.That(entry.GetString($"field-{index}")).IsEqualTo($"value-{index}");
                await Assert.That((await client.Streams.PendingSummaryAsync("s", "g")).Count).IsEqualTo(0);
            }
            await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(1);
        }
    }

    [Test]
    public async Task DeletedSourceCompletionAcknowledgesOnlyTheFencedAttempt()
    {
        await using var real = await RespireClient.ConnectAsync(redis.ConnectionString);
        await using var server = new RespireFakeServer();
        await using var fake = await RespireClient.ConnectAsync(server.CreateOptions());
        foreach (var client in new[] { real, fake })
        {
            await client.Streams.AddAsync("s", new StreamAddOptions { Id = "1-0" }, ("payload", "body"));
            await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
            await client.Streams.ReadGroupOnceAsync("s", "g", "owner");
            using (await client.ExecuteAsync("XDEL", ["s", "1-0"])) { }
            await Assert.That(await CompleteAsync(client, "old", 1)).IsEqualTo(0);
            await Assert.That(await CompleteAsync(client, "owner", 2)).IsEqualTo(0);
            await Assert.That((await client.Streams.PendingSummaryAsync("s", "g")).Count).IsEqualTo(1);
            await Assert.That(await CompleteAsync(client, "owner", 1)).IsEqualTo(-1);
            await Assert.That((await client.Streams.PendingSummaryAsync("s", "g")).Count).IsEqualTo(0);
            await Assert.That(await client.Streams.CountAsync("dlq")).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WrongTypeDeadLetterDoesNotAcknowledgeOrMutate(int protocol)
    {
        await using var real = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        await using var server = new RespireFakeServer();
        await using var fake = await RespireClient.ConnectAsync(server.CreateOptions()
            with { Protocol = (RespProtocol)protocol });
        foreach (var client in new[] { real, fake })
        {
            await client.Streams.AddAsync("s", new StreamAddOptions { Id = "1-0" }, ("payload", "body"));
            await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
            await client.Streams.ReadGroupOnceAsync("s", "g", "owner");
            await client.Strings.SetAsync("dlq", "keep");
            await Assert.That(async () => await CompleteAsync(client, "owner", 1)).Throws<RespireServerException>();
            await Assert.That((await client.Streams.PendingSummaryAsync("s", "g")).Count).IsEqualTo(1);
            await Assert.That(await client.Strings.GetAsync<string>("dlq")).IsEqualTo("keep");
            await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("xadd", false)]
    [Arguments("xack", false)]
    [Arguments("xack", true)]
    public async Task AclPreflightRejectsBothMutationsBeforeDeadLetterWrite(string deniedCommand, bool deletedSource)
    {
        await using var admin = await RespireClient.ConnectAsync(redis.ConnectionString);
        await admin.Streams.AddAsync("s", new StreamAddOptions { Id = "1-0" }, ("payload", "body"));
        await admin.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
        await admin.Streams.ReadGroupOnceAsync("s", "g", "owner");
        if (deletedSource)
        {
            using var deleted = await admin.ExecuteAsync("XDEL", ["s", "1-0"]);
        }
        var username = "dlq-" + Guid.NewGuid().ToString("N");
        using (await admin.ExecuteAsync("ACL SETUSER", [username, "on", ">dlq-test-password", "~*", "+@all", "-" + deniedCommand])) { }
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = username, Password = "dlq-test-password" });
            await Assert.That(async () => await CompleteAsync(restricted, "owner", 1)).Throws<RespireServerException>();
            await Assert.That((await admin.Streams.PendingSummaryAsync("s", "g")).Count).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync("dlq")).IsEqualTo(0);
            await Assert.That(await CompleteAsync(admin, "owner", 1)).IsEqualTo(deletedSource ? -1 : 1);
        }
        finally { using var deleted = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    [Arguments(false, "nack")]
    [Arguments(true, "nack")]
    [Arguments(false, "throw")]
    [Arguments(true, "throw")]
    [Arguments(false, "deserialize")]
    [Arguments(true, "deserialize")]
    [Arguments(false, "explicit")]
    [Arguments(true, "explicit")]
    public async Task StoppedPoisonConsumerEventuallyDeadLettersWithinPolicy(bool useFake, string outcome)
    {
        await using var server = new RespireFakeServer();
        await using var root = await RespireClient.ConnectAsync(useFake ? server.CreateOptions() : RespireOptions.Parse(redis.ConnectionString));
        var client = root.WithKeyPrefix("{worker}tenant:");
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("payload", new byte[] { 0xff, 0, 13, 10 }));
        var abandoned = new HandlerState { Park = true };
        await using (var stopped = CreateWorker(client, abandoned, "nack", 2))
        {
            var worker = stopped.GetServices<IHostedService>().Single();
            await worker.StartAsync(CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await abandoned.Entered.Reader.ReadAsync(deadline.Token);
            await worker.StopAsync(new CancellationToken(canceled: true));
        }
        var recovered = new HandlerState();
        await using var provider = CreateWorker(client, recovered, outcome, 2);
        var recovery = provider.GetServices<IHostedService>().Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await recovery.StartAsync(timeout.Token);
        try
        {
            while (await client.Streams.CountAsync("dlq", timeout.Token) != 1)
                await Task.Delay(10, timeout.Token);
            var entry = (await client.Streams.ReadAsync("dlq", cancellationToken: timeout.Token)).Single();
            await Assert.That(entry["payload"].SequenceEqual(new byte[] { 0xff, 0, 13, 10 })).IsTrue();
            await Assert.That(entry.GetString("_respire.source_id")).IsEqualTo("1-0");
            await Assert.That(entry.GetString("_respire.attempt")).IsEqualTo("2");
            await Assert.That((await client.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
            await Assert.That(recovered.Attempts).IsEqualTo(outcome == "deserialize" ? 0 : 1);
        }
        finally { await recovery.StopAsync(timeout.Token); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AbandonedFinalAttemptDeadLettersWithoutInvokingHandlerAgain(bool useFake)
    {
        await using var server = new RespireFakeServer();
        await using var root = await RespireClient.ConnectAsync(useFake ? server.CreateOptions() : RespireOptions.Parse(redis.ConnectionString));
        var client = root.WithKeyPrefix("{worker}tenant:");
        await client.Streams.AddAsync("events", ("payload", "body"));
        await client.Streams.CreateGroupAsync("events", "workers", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("events", "workers", "abandoned");
        var state = new HandlerState();
        await using var provider = CreateWorker(client, state, "throw", 1);
        var worker = provider.GetServices<IHostedService>().Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(timeout.Token);
        try
        {
            while (await client.Streams.CountAsync("dlq", timeout.Token) != 1)
                await Task.Delay(10, timeout.Token);
            await Assert.That(state.Attempts).IsEqualTo(0);
            var entry = (await client.Streams.ReadAsync("dlq")).Single();
            await Assert.That(entry.GetString("_respire.reason")).IsEqualTo("delivery-limit");
            await Assert.That(entry.GetString("_respire.attempt")).IsEqualTo("2");
        }
        finally { await worker.StopAsync(timeout.Token); }
    }

    private static ServiceProvider CreateWorker(IRespireClient client, HandlerState state, string outcome, int limit)
    {
        state.Outcome = outcome;
        var services = new ServiceCollection().AddSingleton(client).AddSingleton(state);
        services.AddRespireStreamWorker<PoisonHandler, RespireStreamEntry>("events", "workers",
            entry => outcome == "deserialize" ? throw new FormatException("private payload") : entry, new()
            {
                DeadLetterStream = "dlq", DeliveryLimit = limit,
                MinimumIdleTime = TimeSpan.FromMilliseconds(30), RecoveryPollInterval = TimeSpan.FromMilliseconds(10),
            });
        return services.BuildServiceProvider();
    }

    public sealed class HandlerState
    {
        public bool Park { get; init; }
        public string Outcome { get; set; } = "nack";
        public int Attempts;
        public Channel<bool> Entered { get; } = Channel.CreateUnbounded<bool>();
    }

    public sealed class PoisonHandler(HandlerState state) : IRespireStreamHandler<RespireStreamEntry>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry entry, CancellationToken token)
        {
            Interlocked.Increment(ref state.Attempts);
            state.Entered.Writer.TryWrite(true);
            if (state.Park) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return state.Outcome switch
            {
                "throw" => throw new InvalidOperationException("private payload"),
                "explicit" => RespireStreamWorkerResult.DeadLetter,
                _ => RespireStreamWorkerResult.Nack,
            };
        }
    }
}
