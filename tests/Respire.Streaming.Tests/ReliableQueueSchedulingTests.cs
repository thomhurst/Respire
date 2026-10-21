using System.Globalization;
using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ReliableQueueSchedulingTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SchedulingUsesServerTimeAndCancellationPreventsPromotion(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var before = await ServerMilliseconds(client);
        var id = await queue.ScheduleAsync("later", TimeSpan.FromMinutes(5));
        var after = await ServerMilliseconds(client);
        var due = await Score(client, queue.StreamKey + ":scheduled-due", id);
        await Assert.That(due).IsGreaterThanOrEqualTo(before + 300000);
        await Assert.That(due).IsLessThanOrEqualTo(after + 300000);
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await queue.CancelScheduledAsync(id)).IsTrue();
        await Assert.That(await queue.CancelScheduledAsync(id)).IsFalse();
        await Assert.That(await queue.CancelScheduledAsync("unknown")).IsFalse();
        await AssertPendingCount(client, queue, 0);
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ActivePollingDeliversOnFirstPollAfterDueTimeAndNeverBeforeIt(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var id = await queue.ScheduleAsync("later", TimeSpan.FromMilliseconds(150));
        var due = await Score(client, queue.StreamKey + ":scheduled-due", id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RespireQueueMessage<string> message;
        while (true)
        {
            var beforePoll = await ServerMilliseconds(client);
            var batch = await queue.ReceiveAsync(TimeSpan.FromMinutes(1), cancellationToken: timeout.Token);
            if (batch.Length != 0)
            {
                message = batch.Single();
                break;
            }
            // With no backlog, a receive begun after the deadline must deliver this message.
            await Assert.That(beforePoll).IsLessThan(due);
            await Task.Delay(25, timeout.Token);
        }
        // The lease and promotion use the same server clock inside the atomic receive script.
        var receivedAt = await Score(client, queue.StreamKey + ":due", message.Id.Value) - 60000;
        await Assert.That(receivedAt).IsGreaterThanOrEqualTo(due);
        await Assert.That(message.Payload).IsEqualTo("later");
        await Assert.That(message.DeliveryCount).IsEqualTo(1);
        await Assert.That(await queue.CancelScheduledAsync(id)).IsFalse();
        await Assert.That(await queue.AckAsync(message)).IsTrue();
        await AssertPendingCount(client, queue, 0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentPromotionAcrossClientsCreatesOneReadyEntryPerSchedule(int protocol)
    {
        await using var first = await ConnectAsync(protocol);
        await using var second = await ConnectAsync(protocol);
        var clients = new[] { first.WithKeyPrefix("tenant:"), second.WithKeyPrefix("tenant:") };
        var queues = clients.Select(client => Queue(client)).ToArray();
        var ids = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => queues[0].ScheduleAsync("payload-" + i, TimeSpan.Zero).AsTask()));
        await Assert.That(ids.Distinct().Count()).IsEqualTo(40);
        var batches = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => queues[i % 2].ReceiveAsync(TimeSpan.FromMinutes(1), max: 10).AsTask()));
        var messages = batches.SelectMany(batch => batch).ToArray();
        await Assert.That(messages.Length).IsEqualTo(40);
        await Assert.That(messages.Select(message => message.Id).Distinct().Count()).IsEqualTo(40);
        await Assert.That(messages.Select(message => message.Payload).Distinct().Count()).IsEqualTo(40);
        await Assert.That(messages.All(message => message.DeliveryCount == 1)).IsTrue();
        await Assert.That(batches.All(batch => batch.Length <= 10)).IsTrue();
        await Assert.That(await clients[0].Streams.CountAsync(queues[0].StreamKey)).IsEqualTo(40);
        await Assert.That(await first.Streams.CountAsync(queues[0].StreamKey)).IsEqualTo(0);
        await AssertPendingCount(clients[0], queues[0], 0);
        foreach (var message in messages)
            await Assert.That(await queues[0].AckAsync(message)).IsTrue();
        await Assert.That((await queues[0].ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CancellationRacingPromotionHasExactlyOneWinner(int protocol)
    {
        await using var first = await ConnectAsync(protocol);
        await using var second = await ConnectAsync(protocol);
        var producer = Queue(first);
        var consumer = Queue(second);
        for (var i = 0; i < 24; i++)
        {
            var id = await producer.ScheduleAsync("payload", TimeSpan.Zero);
            var cancel = producer.CancelScheduledAsync(id).AsTask();
            var receive = consumer.ReceiveAsync(TimeSpan.FromMinutes(1)).AsTask();
            await Task.WhenAll(cancel, receive);
            var messages = await receive;
            await Assert.That(messages.Length).IsEqualTo(await cancel ? 0 : 1);
            foreach (var message in messages)
                await Assert.That(await consumer.AckAsync(message)).IsTrue();
            await Assert.That(await producer.CancelScheduledAsync(id)).IsFalse();
            await Assert.That((await consumer.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        }
        await AssertPendingCount(first, producer, 0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PromotionBehindReadyBacklogEndsCancellationBeforeDelivery(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.SendAsync("ready");
        var id = await queue.ScheduleAsync("scheduled", TimeSpan.Zero);
        var ready = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(ready.Payload).IsEqualTo("ready");
        await Assert.That(await queue.CancelScheduledAsync(id)).IsFalse();
        await AssertPendingCount(client, queue, 0);
        var scheduled = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(scheduled.Payload).IsEqualTo("scheduled");
        await Assert.That(scheduled.DeliveryCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PendingScheduleSurvivesClientRestartAndUnacknowledgedDeliveryIsFenced(int protocol)
    {
        byte[] payload = [0, 255, 128, 13, 10, 0, 42];
        string scheduledId;
        await using (var producer = await ConnectAsync(protocol))
        {
            var queue = new RespireReliableQueue<byte[]>(producer, "binary", bytes => bytes, bytes => bytes);
            scheduledId = await queue.ScheduleAsync(payload, TimeSpan.Zero);
        }
        RespireQueueMessage<byte[]> original;
        await using (var crashedConsumer = await ConnectAsync(protocol))
        {
            var queue = new RespireReliableQueue<byte[]>(crashedConsumer, "binary", bytes => bytes, bytes => bytes);
            original = (await queue.ReceiveAsync(TimeSpan.FromMilliseconds(100))).Single();
            await Assert.That(original.Payload.SequenceEqual(payload)).IsTrue();
            // Simulate processing followed by loss of the consumer without ACK.
        }
        await using var recoveryClient = await ConnectAsync(protocol);
        var recovery = new RespireReliableQueue<byte[]>(recoveryClient, "binary", bytes => bytes, bytes => bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RespireQueueMessage<byte[]> current;
        while (true)
        {
            var batch = await recovery.ReceiveAsync(TimeSpan.FromMinutes(1), cancellationToken: timeout.Token);
            if (batch.Length != 0) { current = batch.Single(); break; }
            await Task.Delay(10, timeout.Token);
        }
        await Assert.That(current.Id).IsEqualTo(original.Id);
        await Assert.That(current.Payload.SequenceEqual(payload)).IsTrue();
        await Assert.That(current.DeliveryCount).IsEqualTo(2);
        await Assert.That(current.Receipt).IsNotEqualTo(original.Receipt);
        await Assert.That(await recovery.CancelScheduledAsync(scheduledId)).IsFalse();
        await Assert.That(await recovery.AckAsync(original)).IsFalse();
        await Assert.That(await recovery.NackAsync(original)).IsFalse();
        await Assert.That(await recovery.AckAsync(current)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PromotedMessageRetainsDelayedNackAndDeadLetterBehavior(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = new RespireReliableQueue<string>(client, "queue",
            Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, deliveryLimit: 2);
        var scheduled = await queue.ScheduleAsync("poison", TimeSpan.Zero);
        var first = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(await queue.NackAsync(first, TimeSpan.FromMinutes(5))).IsTrue();
        await Assert.That(await queue.CancelScheduledAsync(scheduled)).IsFalse();
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        // Advance the rejection deadline without waiting five minutes.
        using (await client.ExecuteAsync("ZADD", [queue.StreamKey + ":due", 0, first.Id.Value])) { }
        var second = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(second.DeliveryCount).IsEqualTo(2);
        await Assert.That(await queue.AckAsync(first)).IsFalse();
        await Assert.That(await queue.NackAsync(second)).IsTrue();
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        var dead = (await client.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single();
        await Assert.That(dead.GetString("payload")).IsEqualTo("poison");
        await Assert.That(dead.GetString("source_id")).IsEqualTo(first.Id.Value);
        await Assert.That(dead.GetString("delivery_count")).IsEqualTo("2");
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InvalidSchedulingDoesNotSerializeOrWrite(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = new RespireReliableQueue<string>(client, "queue",
            _ => throw new FormatException("serializer"), Encoding.UTF8.GetString);
        foreach (var delay in new[] { TimeSpan.FromTicks(-1), TimeSpan.FromMilliseconds((long)int.MaxValue + 1) })
            await Assert.That(async () => await queue.ScheduleAsync("payload", delay)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await queue.ScheduleAsync("payload", TimeSpan.Zero, new(true)))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await queue.ScheduleAsync("payload", TimeSpan.Zero)).Throws<FormatException>();
        foreach (var id in new[] { "", " " })
            await Assert.That(async () => await queue.CancelScheduledAsync(id)).Throws<ArgumentException>();
        await Assert.That(async () => await queue.CancelScheduledAsync("id", new(true))).Throws<OperationCanceledException>();
        await AssertPendingCount(client, queue, 0);
    }

    [Test]
    [Arguments(2, ":scheduled")]
    [Arguments(3, ":scheduled")]
    [Arguments(2, ":scheduled-due")]
    [Arguments(3, ":scheduled-due")]
    public async Task WrongSchedulingTypeLeavesScheduleAndReadyMessageRecoverable(int protocol, string suffix)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var id = await queue.ScheduleAsync("scheduled", TimeSpan.Zero);
        var ready = await queue.SendAsync("ready");
        var key = queue.StreamKey + suffix;
        using (await client.ExecuteAsync("RENAME", [key, key + ":backup"])) { }
        await client.Strings.SetAsync(key, "wrong type");
        await Assert.That(async () => await queue.ScheduleAsync("new", TimeSpan.Zero)).Throws<RespireServerException>();
        await Assert.That(async () => await queue.CancelScheduledAsync(id)).Throws<RespireServerException>();
        await Assert.That(async () => await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await client.Keys.DeleteAsync(key);
        using (await client.ExecuteAsync("RENAME", [key + ":backup", key])) { }
        var messages = await queue.ReceiveAsync(TimeSpan.FromMinutes(1), max: 2);
        await Assert.That(messages.Length).IsEqualTo(2);
        await Assert.That(messages[0].Id).IsEqualTo(ready);
        await Assert.That(messages[1].Payload).IsEqualTo("scheduled");
        await AssertPendingCount(client, queue, 0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FailedPromotionAppendRetainsPendingSchedule(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.ScheduleAsync("scheduled", TimeSpan.Zero);
        // Fault injection: an exhausted stream ID makes XADD fail after permission/type preflight.
        using (await client.ExecuteAsync("XADD",
            [queue.StreamKey, "18446744073709551615-18446744073709551615", "payload", "sentinel"])) { }
        await Assert.That(async () => await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Throws<RespireServerException>();
        await AssertPendingCount(client, queue, 1);
        await client.Keys.DeleteAsync(queue.StreamKey);
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single().Payload).IsEqualTo("scheduled");
        await AssertPendingCount(client, queue, 0);
    }

    [Test]
    [Arguments(2, "schedule", "zadd")]
    [Arguments(3, "schedule", "zadd")]
    [Arguments(2, "cancel", "hdel")]
    [Arguments(3, "cancel", "hdel")]
    [Arguments(2, "receive", "hdel")]
    [Arguments(3, "receive", "hdel")]
    [Arguments(2, "receive", "xadd")]
    [Arguments(3, "receive", "xadd")]
    public async Task DeniedSchedulingWriteLeavesOriginalStateRetryable(int protocol, string operation, string deniedCommand)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespireProtocol)protocol, AllowAdmin = true });
        var username = "schedule-" + Guid.NewGuid().ToString("N");
        var queue = Queue(admin);
        var id = operation == "schedule" ? null : await queue.ScheduleAsync("payload", TimeSpan.Zero);
        await admin.Server.AclSetUserAsync(username, ["on", ">password", "~*", "+@all", "-" + deniedCommand]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Protocol = (RespireProtocol)protocol, Username = username, Password = "password" });
            var denied = Queue(restricted);
            if (operation == "schedule")
                await Assert.That(async () => await denied.ScheduleAsync("payload", TimeSpan.Zero)).Throws<RespireServerException>();
            else if (operation == "cancel")
                await Assert.That(async () => await denied.CancelScheduledAsync(id!)).Throws<RespireServerException>();
            else
                await Assert.That(async () => await denied.ReceiveAsync(TimeSpan.FromMinutes(1))).Throws<RespireServerException>();
            await AssertPendingCount(admin, queue, operation == "schedule" ? 0 : 1);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
            await admin.Server.AclSetUserAsync(username, ["+" + deniedCommand]);
            if (operation == "schedule") await denied.ScheduleAsync("payload", TimeSpan.Zero);
            if (operation == "cancel")
                await Assert.That(await denied.CancelScheduledAsync(id!)).IsTrue();
            else
                await Assert.That((await denied.ReceiveAsync(TimeSpan.FromMinutes(1))).Single().Payload).IsEqualTo("payload");
            await AssertPendingCount(admin, queue, 0);
        }
        finally { using var deleted = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });

    private static RespireReliableQueue<string> Queue(IRespireClient client)
        => new(client, "queue", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString);

    private static async Task AssertPendingCount(IRespireClient client, RespireReliableQueue<string> queue, int expected)
    {
        await Assert.That(await client.Hashes.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(expected);
        await Assert.That(await client.SortedSets.CountAsync(queue.StreamKey + ":scheduled-due")).IsEqualTo(expected);
    }

    private static async Task<long> Score(IRespireClient client, string key, string id)
    {
        using var result = await client.ExecuteAsync("ZSCORE", [key, id]);
        return long.Parse(result.AsString(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ServerMilliseconds(IRespireClient client)
    {
        using var time = await client.ExecuteAsync("TIME");
        return long.Parse(time[0].AsString(), CultureInfo.InvariantCulture) * 1000
            + long.Parse(time[1].AsString(), CultureInfo.InvariantCulture) / 1000;
    }
}
