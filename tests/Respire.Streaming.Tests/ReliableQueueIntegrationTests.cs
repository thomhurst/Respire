using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ReliableQueueIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AcknowledgementRemovesOnlyItsDeliveryAndWorksAcrossClients(int protocol)
    {
        await using var producer = await ConnectAsync(protocol);
        await using var consumer = await ConnectAsync(protocol);
        var queue = Queue(producer);
        var other = Queue(consumer);
        var first = await queue.SendAsync("first");
        var second = await queue.SendAsync("second");
        var message = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(message.Id).IsEqualTo(first);
        await Assert.That(message.Payload).IsEqualTo("first");
        await Assert.That(message.DeliveryCount).IsEqualTo(1);
        await Assert.That(string.IsNullOrEmpty(message.Receipt)).IsFalse();
        await Assert.That(await other.AckAsync(message)).IsTrue();
        await Assert.That(await queue.AckAsync(message)).IsFalse();
        await Assert.That(await queue.NackAsync(message)).IsFalse();
        var remaining = (await other.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(remaining.Id).IsEqualTo(second);
        await Assert.That(await other.AckAsync(remaining)).IsTrue();
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        await Assert.That(await producer.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReceivingWithShorterVisibilityDoesNotStealAnExistingLease(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var id = await queue.SendAsync("payload");
        var before = await ServerMilliseconds(client);
        var message = (await queue.ReceiveAsync(TimeSpan.FromMinutes(5))).Single();
        var after = await ServerMilliseconds(client);
        var due = await Due(client, queue, id);
        await Assert.That(due).IsGreaterThanOrEqualTo(before + 300000);
        await Assert.That(due).IsLessThanOrEqualTo(after + 300000);
        await Assert.That((await Queue(client).ReceiveAsync(TimeSpan.FromMilliseconds(1))).Length).IsEqualTo(0);
        await Assert.That(await Due(client, queue, id)).IsEqualTo(due);
        await Assert.That(await queue.AckAsync(message)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExpiryRevokesReceiptsBeforeAndAfterRedelivery(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.SendAsync("payload");
        var first = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await MakeDue(client, queue, first.Id);
        await Assert.That(await queue.AckAsync(first)).IsFalse();
        await Assert.That(await queue.NackAsync(first)).IsFalse();
        var second = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(second.Payload).IsEqualTo(first.Payload);
        await Assert.That(second.DeliveryCount).IsEqualTo(2);
        await Assert.That(second.Receipt).IsNotEqualTo(first.Receipt);
        await Assert.That(await queue.AckAsync(first)).IsFalse();
        await Assert.That(await queue.NackAsync(first)).IsFalse();
        await Assert.That(await queue.AckAsync(second)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task VisibilityTimeoutRedeliversWithoutMetadataIntervention(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.SendAsync("payload");
        var original = (await queue.ReceiveAsync(TimeSpan.FromMilliseconds(100))).Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RespireQueueMessage<string>[] redelivered;
        do
        {
            redelivered = await queue.ReceiveAsync(TimeSpan.FromMinutes(1), cancellationToken: deadline.Token);
            if (redelivered.Length == 0) await Task.Delay(10, deadline.Token);
        } while (redelivered.Length == 0);
        var current = redelivered.Single();
        await Assert.That(current.Id).IsEqualTo(original.Id);
        await Assert.That(current.Payload).IsEqualTo(original.Payload);
        await Assert.That(current.DeliveryCount).IsEqualTo(2);
        await Assert.That(current.Receipt).IsNotEqualTo(original.Receipt);
        await Assert.That(await queue.AckAsync(original)).IsFalse();
        await Assert.That(await queue.NackAsync(original)).IsFalse();
        await Assert.That(await queue.AckAsync(current)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InvalidArgumentsAndCancellationDoNotMutateQueue(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        foreach (var name in new[] { "", " ", "queue{tag}", "queue}" })
            await Assert.That(() => Queue(client, name)).Throws<ArgumentException>();
        foreach (var limit in new[] { 0, -1 })
            await Assert.That(() => Queue(client, deliveryLimit: limit)).Throws<ArgumentOutOfRangeException>();
        var queue = Queue(client);
        foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds((long)int.MaxValue + 1) })
        {
            await Assert.That(async () => await queue.ReceiveAsync(duration)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(async () => await queue.SendAsync("payload", "id", duration)).Throws<ArgumentOutOfRangeException>();
        }
        foreach (var max in new[] { 0, -1, 1001 })
            await Assert.That(async () => await queue.ReceiveAsync(TimeSpan.FromMinutes(1), max)).Throws<ArgumentOutOfRangeException>();
        var cancellationBeforeSerialization = new RespireReliableQueue<string>(client, "queue",
            static _ => throw new InvalidOperationException("Serialization should not run."), Encoding.UTF8.GetString);
        await Assert.That(async () => await cancellationBeforeSerialization.SendAsync("payload", cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await queue.SendAsync("payload");
        await Assert.That(async () => await queue.ReceiveAsync(TimeSpan.FromMinutes(1), cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
        var message = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(message.DeliveryCount).IsEqualTo(1);
        foreach (var delay in new[] { TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds((long)int.MaxValue + 1) })
            await Assert.That(async () => await queue.NackAsync(message, delay)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(await queue.AckAsync(message)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DelayedNackRevokesReceiptAndSchedulesUsingServerTime(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.SendAsync("payload");
        var first = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        var before = await ServerMilliseconds(client);
        await Assert.That(await queue.NackAsync(first, TimeSpan.FromMinutes(5))).IsTrue();
        var after = await ServerMilliseconds(client);
        var due = await Due(client, queue, first.Id);
        await Assert.That(due).IsGreaterThanOrEqualTo(before + 300000);
        await Assert.That(due).IsLessThanOrEqualTo(after + 300000);
        await Assert.That(await queue.AckAsync(first)).IsFalse();
        await Assert.That(await queue.NackAsync(first)).IsFalse();
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        await MakeDue(client, queue, first.Id);
        var second = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(second.DeliveryCount).IsEqualTo(2);
        await Assert.That(await queue.AckAsync(second)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ZeroDelayNackIsImmediatelyAvailableWithANewReceipt(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.SendAsync("payload");
        var first = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(await queue.NackAsync(first)).IsTrue();
        var second = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(second.Receipt).IsNotEqualTo(first.Receipt);
        await Assert.That(second.DeliveryCount).IsEqualTo(2);
        await Assert.That(await queue.AckAsync(first)).IsFalse();
        await Assert.That(await queue.AckAsync(second)).IsTrue();
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task ExhaustedDeliveryMovesToDeadLetterExactlyOnceWhenDue(int protocol, bool nack)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client, deliveryLimit: 2);
        var id = await queue.SendAsync("poison");
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var message = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
            await Assert.That(message.DeliveryCount).IsEqualTo(attempt);
            if (nack) await Assert.That(await queue.NackAsync(message, TimeSpan.FromMinutes(1))).IsTrue();
            await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(0);
            await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
            await MakeDue(client, queue, id);
        }
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        var dead = (await client.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single();
        await Assert.That(dead.GetString("payload")).IsEqualTo("poison");
        await Assert.That(dead.GetString("source_id")).IsEqualTo(id.Value);
        await Assert.That(dead.GetString("delivery_count")).IsEqualTo("2");
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentConsumersReceiveEachMessageOnlyOncePerLease(int protocol)
    {
        await using var first = await ConnectAsync(protocol);
        await using var second = await ConnectAsync(protocol);
        var queues = new[] { Queue(first), Queue(second) };
        var ids = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => queues[0].SendAsync("payload-" + i).AsTask()));
        var batches = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => queues[i % 2].ReceiveAsync(TimeSpan.FromMinutes(1), max: 10).AsTask()));
        var received = batches.SelectMany(batch => batch).ToArray();
        await Assert.That(received.Length).IsEqualTo(40);
        await Assert.That(received.Select(message => message.Id).Distinct().Count()).IsEqualTo(40);
        await Assert.That(received.All(message => ids.Contains(message.Id) && message.DeliveryCount == 1)).IsTrue();
        await Assert.That(batches.All(batch => batch.Length <= 10)).IsTrue();
        await Assert.That((await queues[0].ReceiveAsync(TimeSpan.FromMinutes(1), max: 10)).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentSendsDeduplicateWithoutReplacingPayloadEvenAfterAck(int protocol)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        var ids = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => queue.SendAsync("payload-" + i, "same-id").AsTask()));
        await Assert.That(ids.Distinct().Count()).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        var first = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(await queue.SendAsync("replacement", "same-id")).IsEqualTo(first.Id);
        await Assert.That(await queue.NackAsync(first)).IsTrue();
        var retry = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(retry.Payload).IsEqualTo(first.Payload);
        await Assert.That(await queue.AckAsync(retry)).IsTrue();
        await Assert.That(await queue.SendAsync("replacement after ack", "same-id")).IsEqualTo(first.Id);
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        await Assert.That(await root.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await Queue(client, "other").SendAsync("other", "same-id")).IsNotEqualTo(default(RespireStreamId));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BinaryPayloadSurvivesRedeliveryAndDeadLetter(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = new RespireReliableQueue<byte[]>(client, "binary", static bytes => bytes, static bytes => bytes, deliveryLimit: 2);
        byte[] payload = [0, 255, 128, 13, 10, 0, 42];
        var id = await queue.SendAsync(payload);
        var first = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(first.Payload.SequenceEqual(payload)).IsTrue();
        await queue.NackAsync(first);
        var second = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(second.Payload.SequenceEqual(payload)).IsTrue();
        await queue.NackAsync(second);
        await queue.ReceiveAsync(TimeSpan.FromMinutes(1));
        var dead = (await client.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single();
        await Assert.That(dead["payload"]!.SequenceEqual(payload)).IsTrue();
        await Assert.That(dead.GetString("source_id")).IsEqualTo(id.Value);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DeduplicationWindowDoesNotExtendOnRetryAndExpiryDoesNotDeletePayload(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var id = await queue.SendAsync("original", "application-id", TimeSpan.FromMinutes(5));
        var key = queue.StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("application-id")));
        using var firstDeadline = await client.ExecuteAsync("PEXPIRETIME", [key]);
        await Assert.That(await queue.SendAsync("replacement", "application-id", TimeSpan.FromDays(1))).IsEqualTo(id);
        using var retryDeadline = await client.ExecuteAsync("PEXPIRETIME", [key]);
        await Assert.That(retryDeadline.AsInteger()).IsEqualTo(firstDeadline.AsInteger());
        using (await client.ExecuteAsync("PEXPIREAT", [key, 1])) { }
        var nextId = await queue.SendAsync("next", "application-id");
        await Assert.That(nextId).IsNotEqualTo(id);
        var messages = await queue.ReceiveAsync(TimeSpan.FromMinutes(1), max: 2);
        await Assert.That(messages.Length).IsEqualTo(2);
        await Assert.That(messages[0].Payload).IsEqualTo("original");
        await Assert.That(messages[1].Payload).IsEqualTo("next");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SerializerAndDeserializerFailuresDoNotLoseMessages(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var failingSerializer = new RespireReliableQueue<string>(client, "queue",
            static _ => throw new FormatException("serialize"), Encoding.UTF8.GetString);
        await Assert.That(async () => await failingSerializer.SendAsync("bad", "id")).Throws<FormatException>();
        var queue = Queue(client);
        var id = await queue.SendAsync("payload", "id");
        var failingDeserializer = new RespireReliableQueue<string>(client, "queue",
            Encoding.UTF8.GetBytes, static _ => throw new FormatException("deserialize"));
        await Assert.That(async () => await failingDeserializer.ReceiveAsync(TimeSpan.FromMinutes(1))).Throws<FormatException>();
        await MakeDue(client, queue, id);
        var recovered = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(recovered.Id).IsEqualTo(id);
        await Assert.That(recovered.Payload).IsEqualTo("payload");
        await Assert.That(await queue.AckAsync(recovered)).IsTrue();
    }

    [Test]
    [Arguments(2, ":due")]
    [Arguments(3, ":due")]
    [Arguments(2, ":receipts")]
    [Arguments(3, ":receipts")]
    [Arguments(2, ":attempts")]
    [Arguments(3, ":attempts")]
    public async Task WrongMetadataTypeDoesNotConsumeTheAvailableMessage(int protocol, string suffix)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var id = await queue.SendAsync("payload");
        var metadataKey = queue.StreamKey + suffix;
        using var exists = await client.ExecuteAsync("EXISTS", [metadataKey]);
        var hadMetadata = exists.AsInteger() != 0;
        if (hadMetadata) { using var moved = await client.ExecuteAsync("RENAME", [metadataKey, metadataKey + ":backup"]); }
        await client.Strings.SetAsync(metadataKey, "wrong type");
        await Assert.That(async () => await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Throws<RespireServerException>();
        await client.Keys.DeleteAsync(metadataKey);
        if (hadMetadata) { using var restored = await client.ExecuteAsync("RENAME", [metadataKey + ":backup", metadataKey]); }
        var message = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
        await Assert.That(message.Id).IsEqualTo(id);
        await Assert.That(message.DeliveryCount).IsEqualTo(1);
        await Assert.That(await queue.AckAsync(message)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WrongDeadLetterTypeRetainsExhaustedMessageUntilRepair(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client, deliveryLimit: 1);
        var id = await queue.SendAsync("poison");
        await queue.NackAsync((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single());
        await client.Strings.SetAsync(queue.DeadLetterStreamKey, "wrong type");
        await Assert.That(async () => await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await client.Keys.DeleteAsync(queue.DeadLetterStreamKey);
        await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
        await Assert.That((await client.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single().GetString("source_id"))
            .IsEqualTo(id.Value);
    }

    [Test]
    [Arguments(2, "send", "set")]
    [Arguments(3, "send", "set")]
    [Arguments(2, "receive", "hset")]
    [Arguments(3, "receive", "hset")]
    [Arguments(2, "ack", "xdel")]
    [Arguments(3, "ack", "xdel")]
    [Arguments(2, "nack", "zadd")]
    [Arguments(3, "nack", "zadd")]
    public async Task DeniedWriteLeavesOperationRetryable(int protocol, string operation, string deniedCommand)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespireProtocol)protocol, AllowAdmin = true });
        var username = "reliable-" + Guid.NewGuid().ToString("N");
        var queue = Queue(admin);
        await admin.Server.AclSetUserAsync(username, ["on", ">queue-password", "~*", "+@all", "-" + deniedCommand]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Protocol = (RespireProtocol)protocol, Username = username, Password = "queue-password" });
            var restrictedQueue = Queue(restricted);
            if (operation == "send")
            {
                await Assert.That(async () => await restrictedQueue.SendAsync("payload", "id")).Throws<RespireServerException>();
                await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
                await admin.Server.AclSetUserAsync(username, ["+" + deniedCommand]);
                var id = await restrictedQueue.SendAsync("payload", "id");
                await Assert.That(await restrictedQueue.SendAsync("replacement", "id")).IsEqualTo(id);
                await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
            }
            else if (operation == "receive")
            {
                var id = await queue.SendAsync("payload");
                await Assert.That(async () => await restrictedQueue.ReceiveAsync(TimeSpan.FromMinutes(1))).Throws<RespireServerException>();
                await admin.Server.AclSetUserAsync(username, ["+" + deniedCommand]);
                var message = (await restrictedQueue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
                await Assert.That(message.Id).IsEqualTo(id);
                await Assert.That(message.DeliveryCount).IsEqualTo(1);
            }
            else
            {
                await queue.SendAsync("payload");
                var message = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
                await Assert.That(async () => await (operation == "ack"
                    ? restrictedQueue.AckAsync(message) : restrictedQueue.NackAsync(message))).Throws<RespireServerException>();
                await admin.Server.AclSetUserAsync(username, ["+" + deniedCommand]);
                await Assert.That(await (operation == "ack"
                    ? restrictedQueue.AckAsync(message) : restrictedQueue.NackAsync(message))).IsTrue();
                await Assert.That(await restrictedQueue.AckAsync(message)).IsFalse();
                if (operation == "nack")
                {
                    var redelivered = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
                    await Assert.That(redelivered.Id).IsEqualTo(message.Id);
                    await Assert.That(redelivered.DeliveryCount).IsEqualTo(2);
                }
            }
        }
        finally { using var deleted = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    [Arguments(2, "info")]
    [Arguments(3, "info")]
    [Arguments(2, "xackdel")]
    [Arguments(3, "xackdel")]
    public async Task DeniedOptionalCapabilityUsesCompatibleAcknowledgement(int protocol, string deniedCommand)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespireProtocol)protocol, AllowAdmin = true });
        if (deniedCommand == "xackdel")
        {
            using var command = await admin.ExecuteAsync("COMMAND INFO", ["XACKDEL"]);
            if (command[0].IsNull) return;
        }
        var username = "reliable-fallback-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">queue-password", "~*", "+@all", "-" + deniedCommand]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Protocol = (RespireProtocol)protocol, Username = username, Password = "queue-password" });
            var queue = Queue(restricted, deliveryLimit: 1);
            await queue.SendAsync("acknowledge");
            await Assert.That(await queue.AckAsync((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single())).IsTrue();
            var poison = await queue.SendAsync("poison");
            await queue.NackAsync((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Single());
            await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
            await Assert.That((await admin.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single().GetString("source_id"))
                .IsEqualTo(poison.Value);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        }
        finally { using var deleted = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });

    private static RespireReliableQueue<string> Queue(IRespireClient client, string name = "queue", int deliveryLimit = 5)
        => new(client, name, Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, deliveryLimit);

    private static async Task MakeDue(IRespireClient client, RespireReliableQueue<string> queue, RespireStreamId id)
    {
        // Fault injection advances this delivery's visibility or delay deadline without wall-clock sleeps.
        using var result = await client.ExecuteAsync("ZADD", [queue.StreamKey + ":due", 0, id.Value]);
    }

    private static async Task<long> Due(IRespireClient client, RespireReliableQueue<string> queue, RespireStreamId id)
    {
        using var result = await client.ExecuteAsync("ZSCORE", [queue.StreamKey + ":due", id.Value]);
        return long.Parse(result.AsString(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ServerMilliseconds(IRespireClient client)
    {
        using var time = await client.ExecuteAsync("TIME");
        return long.Parse(time[0].AsString(), CultureInfo.InvariantCulture) * 1000
            + long.Parse(time[1].AsString(), CultureInfo.InvariantCulture) / 1000;
    }
}
