using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JobGroupIntegrationTests(RedisTestContainer redis)
{
    private const string ConsumerGroup = RespireJobQueue<string>.ConsumerGroup;
    private const string ApplicationGroup = " tenant:{foreign}:é/東京\0😀 ";

    [Test]
    [Arguments(2, "tenant:")]
    [Arguments(3, "{tenant}:")]
    public async Task ScheduledGroupSurvivesDeduplicationAndDelayedPromotion(int protocol, string prefix)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix(prefix);
        var queue = Queue(client);
        var first = await queue.EnqueueScheduledAsync("id", "original", new()
        {
            Group = ApplicationGroup, Delay = TimeSpan.FromHours(1), Priority = 5,
        });
        var duplicate = await queue.EnqueueScheduledAsync("id", "replacement", new() { Group = "other" });
        await Assert.That(duplicate.Enqueued).IsFalse();
        await Assert.That(duplicate.EntryId).IsEqualTo(first.EntryId);
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        await Assert.That((await Read(client, queue, "owner")).Length).IsEqualTo(0);
        var staged = (await client.Streams.ReadAsync(queue.StreamKey + ":scheduled")).Single();
        await Assert.That(staged.GetString("_respire.job.group")).IsEqualTo(ApplicationGroup);

        // Advance this test-owned lexicographic due member without a wall-clock delay.
        var index = client.ResolveKey(queue.StreamKey + ":group-jobs").ToBytes();
        using var members = await root.ExecuteAsync("ZRANGE", [index, 0, -1]);
        var member = members[0].AsString();
        var separator = member.IndexOf('|');
        var dueNow = member[..(separator + 1)] + "0000000000000000" + member[(separator + 17)..];
        using (await root.ExecuteAsync("ZREM", [index, member])) { }
        using (await root.ExecuteAsync("ZADD", [index, 0, dueNow])) { }
        await Assert.That((await Read(client, queue, "owner")).Length).IsEqualTo(1);
        var ready = (await client.Streams.ReadAsync(queue.StreamKey)).Single();
        await Assert.That(ready.GetString("_respire.job.group")).IsEqualTo(ApplicationGroup);
        await Assert.That(ready.GetString("_respire.job.payload")).IsEqualTo("original");
        await Assert.That(ready.GetString("_respire.job.priority")).IsEqualTo("5");
        await Assert.That((await queue.EnqueueScheduledAsync("id", "replacement", new())).Enqueued).IsFalse();
        await Assert.That(await root.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task GroupSurvivesDurableRetryRecoveryAndDeadLetter(bool grouped)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var expectedGroup = grouped ? ApplicationGroup : null;
        await queue.EnqueueScheduledAsync("id", "payload", new() { Group = expectedGroup });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        await Promote(client, queue);
        var original = (await Read(client, queue, "retry-owner")).Single();
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("Retry"), Keys(queue),
            [ConsumerGroup, "retry-owner", original.Id.Value, 1, 0, 0])).IsEqualTo(1);
        var staged = (await client.Streams.ReadAsync(queue.StreamKey + ":scheduled")).Single();
        await Assert.That(staged.GetString("_respire.job.group")).IsEqualTo(expectedGroup);
        await Promote(client, queue);

        // A separate consumer disappears with the retried delivery pending.
        RespireStreamId abandoned;
        await using (var crashed = await ConnectAsync())
            abandoned = (await Read(crashed, queue, "crashed")).Single().Id;
        await Assert.That(abandoned).IsNotEqualTo(original.Id);

        var handler = new Handler();
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(handler);
        services.AddRespireJobQueue<Handler, string>("groups", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new()
            {
                ConsumerName = "recovery", MinimumIdleTime = TimeSpan.FromMilliseconds(50),
                RecoveryPollInterval = TimeSpan.FromMilliseconds(20), ReadWait = TimeSpan.FromMilliseconds(20),
                DeliveryLimit = 5,
            });
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            var first = await Task.WhenAny(handler.Received.Task, ((BackgroundService)worker).ExecuteTask!).WaitAsync(deadline.Token);
            await first;
            var delivery = await handler.Received.Task.WaitAsync(deadline.Token);
            await Assert.That(delivery.Group).IsEqualTo(expectedGroup);
            await Assert.That(delivery.Id).IsEqualTo("id");
            await Assert.That(delivery.Payload).IsEqualTo("payload");
            await Assert.That(delivery.EntryId).IsEqualTo(abandoned);
            while (await client.Streams.CountAsync(queue.DeadLetterStreamKey) == 0)
                await Task.Delay(10, deadline.Token);
            var dead = (await client.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single();
            await Assert.That(dead.GetString("_respire.job.group")).IsEqualTo(expectedGroup);
            await Assert.That(dead.GetString("_respire.group")).IsEqualTo(ConsumerGroup);
            await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(0);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task ImmediateUngroupedEnqueueRetainsItsOriginalEnvelopeWhenDuplicateSpecifiesGroup()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var first = await queue.EnqueueAsync("id", "original");
        var duplicate = await queue.EnqueueScheduledAsync("id", "replacement", new() { Group = ApplicationGroup });
        await Assert.That(duplicate.Enqueued).IsFalse();
        await Assert.That(duplicate.EntryId).IsEqualTo(first.EntryId);
        var ready = (await client.Streams.ReadAsync(queue.StreamKey)).Single();
        await Assert.That(ready.GetString("_respire.job.group")).IsNull();
        await Assert.That(ready.GetString("_respire.job.payload")).IsEqualTo("original");
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
    }

    public sealed class Handler : IRespireStreamHandler<RespireJob<string>>
    {
        public TaskCompletionSource<RespireJob<string>> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> message, CancellationToken cancellationToken)
        {
            Received.TrySetResult(message);
            return ValueTask.FromResult(RespireStreamWorkerResult.DeadLetter);
        }
    }

    private async Task<RespireClient> ConnectAsync(int protocol = 3)
        => await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });

    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "groups", Encoding.UTF8.GetBytes);
    internal static RespireKey[] Keys(RespireJobQueue<string> queue) => [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due",
        queue.StreamKey + ":group-order", queue.StreamKey + ":group-jobs", queue.StreamKey + ":group-cursor"];
    internal static async Task<RespireStreamEntry[]> Read(IRespireClient client, RespireJobQueue<string> queue, string consumer,
        int count = 1, int candidates = 64, RespireScript? script = null)
    {
        using var reply = await client.Scripts.ExecuteAsync(script ?? Script("ReadGroups"), Keys(queue),
            [ConsumerGroup, consumer, count, candidates]);
        var entries = new RespireStreamEntry[reply.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var row = reply[i];
            var values = row[1];
            var fields = new KeyValuePair<string, byte[]>[values.Count / 2];
            for (var j = 0; j < fields.Length; j++) fields[j] = new(values[j * 2].AsString(), values[j * 2 + 1].AsBytes());
            entries[i] = new(new(row[0].AsString()), fields);
        }
        return entries;
    }
    private static ValueTask<long> Promote(IRespireClient client, RespireJobQueue<string> queue)
        => client.Scripts.ExecuteIntegerAsync(Script("Promote"), Keys(queue), [64]);
    private static RespireScript Script(string name) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming.JobQueueScheduling")!.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}
