using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ReliableTopicIntegrationTests(RedisTestContainer redis)
{
    private static readonly TimeSpan Visibility = TimeSpan.FromMinutes(5);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OfflineSubscriptionsReceiveEveryPublicationAndShareWorkWithinGroup(int protocol)
    {
        await using var producer = await Connect(protocol);
        await using (var offline = await Connect(protocol))
            _ = await Topic(offline).SubscribeAsync("offline", RespireStreamId.New);
        var topic = Topic(producer);
        var first = await topic.PublishAsync("first");
        var second = await topic.PublishAsync("second");
        await using var consumer = await Connect(protocol);
        var subscription = await Topic(consumer).SubscribeAsync("offline", RespireStreamId.New);
        var competitor = await topic.SubscribeAsync("offline");
        var deliveries = await Task.WhenAll(subscription.ReceiveAsync(Visibility).AsTask(), competitor.ReceiveAsync(Visibility).AsTask());
        await Assert.That(deliveries.SelectMany(x => x).Select(x => x.Id).Distinct().Count()).IsEqualTo(2);
        foreach (var message in deliveries.SelectMany(x => x))
            await Assert.That(await subscription.AckAsync(message)).IsTrue();
        var independent = await topic.SubscribeAsync("independent");
        var copies = await independent.ReceiveAsync(Visibility, 10);
        await Assert.That(copies.Select(x => x.Id).ToArray()).IsEquivalentTo(new[] { first, second });
        await Assert.That(await producer.Streams.CountAsync(topic.StreamKey)).IsEqualTo(2);
        await Assert.That((await competitor.ReceiveAsync(Visibility)).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RetryReceiptsAndDeadLettersAreIsolatedBetweenSubscriptions(int protocol)
    {
        await using var client = await Connect(protocol);
        var topic = Topic(client, deliveryLimit: 2);
        var a = await topic.SubscribeAsync("a");
        var b = await topic.SubscribeAsync("b");
        var id = await topic.PublishAsync("poison");
        var original = (await a.ReceiveAsync(Visibility)).Single();
        var other = (await b.ReceiveAsync(Visibility)).Single();
        await Assert.That(await b.AckAsync(original)).IsFalse();
        await Assert.That(await a.NackAsync(original)).IsTrue();
        await Assert.That(await a.AckAsync(original)).IsFalse();
        var retry = (await a.ReceiveAsync(Visibility)).Single();
        await Assert.That(retry.Id).IsEqualTo(id);
        await Assert.That(retry.DeliveryCount).IsEqualTo(2);
        await Assert.That(retry.Receipt).IsNotEqualTo(original.Receipt);
        await Assert.That(await a.NackAsync(original)).IsFalse();
        await Assert.That(await a.NackAsync(retry)).IsTrue();
        await Assert.That((await a.ReceiveAsync(Visibility)).Length).IsEqualTo(0);
        var dead = (await client.Streams.ReadAsync(a.DeadLetterStreamKey)).Single();
        await Assert.That(dead.GetString("payload")).IsEqualTo("poison");
        await Assert.That(dead.GetString("source_id")).IsEqualTo(id.ToString());
        await Assert.That(dead.GetString("delivery_count")).IsEqualTo("2");
        await Assert.That(dead.GetString("subscription")).IsEqualTo("a");
        await Assert.That(await b.AckAsync(other)).IsTrue();
        await Assert.That(await b.AckAsync(other)).IsFalse();
        await Assert.That(await client.Streams.CountAsync(b.DeadLetterStreamKey)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(topic.StreamKey)).IsEqualTo(1);
        await Assert.That((await a.ReceiveAsync(Visibility)).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExpiryAndDelayedNackRevokeStaleReceipts(int protocol)
    {
        await using var client = await Connect(protocol);
        var topic = Topic(client);
        var subscription = await topic.SubscribeAsync("lease");
        await topic.PublishAsync("leased");
        var original = (await subscription.ReceiveAsync(Visibility)).Single();
        // A different receiver's shorter timeout must not steal a live lease.
        await Assert.That((await subscription.ReceiveAsync(TimeSpan.FromMilliseconds(1))).Length).IsEqualTo(0);
        await MakeDue(client, subscription, original.Id);
        await Assert.That(await subscription.AckAsync(original)).IsFalse();
        var retry = (await subscription.ReceiveAsync(Visibility)).Single();
        await Assert.That(retry.DeliveryCount).IsEqualTo(2);
        await Assert.That(await subscription.AckAsync(original)).IsFalse();
        await Assert.That(await subscription.NackAsync(retry, TimeSpan.FromMinutes(1))).IsTrue();
        await Assert.That((await subscription.ReceiveAsync(Visibility)).Length).IsEqualTo(0);
        await Assert.That(await subscription.AckAsync(retry)).IsFalse();
        await MakeDue(client, subscription, retry.Id);
        var last = (await subscription.ReceiveAsync(Visibility)).Single();
        await Assert.That(last.DeliveryCount).IsEqualTo(3);
        await Assert.That(await subscription.AckAsync(last)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReplayByIdAndTimePreservesOtherSubscriptionsAndRejectsPendingSeek(int protocol)
    {
        await using var client = await Connect(protocol);
        var topic = Topic(client);
        using (var old = await client.ExecuteAsync("XADD", [topic.StreamKey, "1000-0", "payload", "old"])) { }
        using (var next = await client.ExecuteAsync("XADD", [topic.StreamKey, "2000-0", "payload", "next"])) { }
        var subscription = await topic.SubscribeAsync("replay");
        var other = await topic.SubscribeAsync("other");
        var messages = await subscription.ReceiveAsync(Visibility, 10);
        await Assert.That(async () => await subscription.SeekAsync(RespireStreamId.Beginning)).Throws<RespireServerException>();
        foreach (var message in messages) await subscription.AckAsync(message);
        await subscription.SeekAsync(new RespireStreamId("1000-0"));
        var afterId = (await subscription.ReceiveAsync(Visibility)).Single();
        await Assert.That(afterId.Payload).IsEqualTo("next");
        await subscription.AckAsync(afterId);
        await subscription.SeekAsync(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        var afterTime = (await subscription.ReceiveAsync(Visibility)).Single();
        await Assert.That(afterTime.Payload).IsEqualTo("next");
        await Assert.That(afterTime.DeliveryCount).IsEqualTo(1);
        await subscription.AckAsync(afterTime);
        await subscription.SeekAsync(RespireStreamId.New);
        await Assert.That((await subscription.ReceiveAsync(Visibility)).Length).IsEqualTo(0);
        await subscription.SeekAsync(RespireStreamId.Beginning);
        await Assert.That((await subscription.ReceiveAsync(Visibility, 10)).Length).IsEqualTo(2);
        await Assert.That((await other.ReceiveAsync(Visibility, 10)).Length).IsEqualTo(2);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LengthRetentionCleansTrimmedPendingEntriesAndKeepsNewest(int protocol)
    {
        await using var client = await Connect(protocol);
        var topic = Topic(client, maximumLength: 1);
        var subscription = await topic.SubscribeAsync("retained");
        await topic.PublishAsync("trimmed");
        var trimmed = (await subscription.ReceiveAsync(Visibility)).Single();
        await topic.PublishAsync("retained");
        await MakeDue(client, subscription, trimmed.Id);
        // First receive can spend its bounded work budget cleaning the trimmed pending entry.
        await subscription.ReceiveAsync(Visibility);
        var retained = (await subscription.ReceiveAsync(Visibility)).Single();
        await Assert.That(retained.Payload).IsEqualTo("retained");
        await Assert.That(await subscription.AckAsync(trimmed)).IsFalse();
        await subscription.AckAsync(retained);
        await subscription.SeekAsync(RespireStreamId.Beginning);
        await Assert.That(await client.Streams.CountAsync(topic.StreamKey)).IsEqualTo(1);
        await Assert.That((await subscription.ReceiveAsync(Visibility)).Single().Payload).IsEqualTo("retained");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AgeRetentionUsesPublicationTimeAndFutureOnlyStartSkipsHistory(int protocol)
    {
        await using var client = await Connect(protocol);
        var topic = new RespireReliableTopic<string>(client, "age", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            maximumAge: TimeSpan.FromMinutes(1));
        using (var old = await client.ExecuteAsync("XADD", [topic.StreamKey, "1-0", "payload", "old"])) { }
        var subscription = await topic.SubscribeAsync("future", RespireStreamId.New);
        await Assert.That((await subscription.ReceiveAsync(Visibility)).Length).IsEqualTo(0);
        await topic.PublishAsync("current");
        await Assert.That(await client.Streams.CountAsync(topic.StreamKey)).IsEqualTo(1);
        await Assert.That((await subscription.ReceiveAsync(Visibility)).Single().Payload).IsEqualTo("current");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AsyncConsumptionRequiresExplicitAckAndHonorsCancellation(int protocol)
    {
        await using var client = await Connect(protocol);
        var topic = Topic(client);
        var subscription = await topic.SubscribeAsync("enumerator");
        await topic.PublishAsync("one");
        using var stop = new CancellationTokenSource();
        await using var enumerator = subscription.ConsumeAsync(Visibility, cancellationToken: stop.Token).GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current.Payload).IsEqualTo("one");
        await Assert.That(async () => await subscription.SeekAsync(RespireStreamId.Beginning)).Throws<RespireServerException>();
        await subscription.AckAsync(enumerator.Current);
        await stop.CancelAsync();
        await Assert.That(async () => await enumerator.MoveNextAsync()).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SharedBinaryCodecsCompressAndEncryptTopicAndQueuePayloads(int protocol)
    {
        await using var client = await Connect(protocol);
        var key = RandomNumberGenerator.GetBytes(32);
        byte[] Serialize(string value)
        {
            using var output = new MemoryStream();
            using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                compressor.Write(Encoding.UTF8.GetBytes(value));
            using var cipher = Aes.Create();
            cipher.Key = key;
            return [.. cipher.IV, .. cipher.EncryptCbc(output.ToArray(), cipher.IV)];
        }
        string Deserialize(byte[] bytes)
        {
            using var cipher = Aes.Create();
            cipher.Key = key;
            using var input = new MemoryStream(cipher.DecryptCbc(bytes.AsSpan(16), bytes.AsSpan(0, 16)));
            using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(decompressor, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        var topic = new RespireReliableTopic<string>(client, "binary", Serialize, Deserialize, deliveryLimit: 1);
        var queue = new RespireReliableQueue<string>(client, "binary", Serialize, Deserialize);
        var jobs = new RespireJobQueue<string>(client, "binary", Serialize);
        await topic.PublishAsync("東京\0message");
        await queue.SendAsync("東京\0message");
        await jobs.EnqueueAsync("job", "東京\0message");
        var subscription = await topic.SubscribeAsync("binary");
        var message = (await subscription.ReceiveAsync(Visibility)).Single();
        await Assert.That(message.Payload).IsEqualTo("東京\0message");
        await subscription.NackAsync(message);
        await subscription.ReceiveAsync(Visibility);
        var dead = (await client.Streams.ReadAsync(subscription.DeadLetterStreamKey)).Single();
        await Assert.That(Deserialize(dead["payload"]!)).IsEqualTo("東京\0message");
        await Assert.That((await queue.ReceiveAsync(Visibility)).Single().Payload).IsEqualTo("東京\0message");
        var job = (await client.Streams.ReadAsync(jobs.StreamKey)).Single();
        await Assert.That(Deserialize(job["_respire.job.payload"]!)).IsEqualTo("東京\0message");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WrongDeadLetterTypeFailsBeforeLeasingNewMessages(int protocol)
    {
        await using var client = await Connect(protocol);
        var topic = Topic(client);
        var subscription = await topic.SubscribeAsync("types");
        var id = await topic.PublishAsync("safe");
        await client.Strings.SetAsync(subscription.DeadLetterStreamKey, "wrong-type");
        await Assert.That(async () => await subscription.ReceiveAsync(Visibility)).Throws<RespireServerException>();
        await client.Keys.DeleteAsync(subscription.DeadLetterStreamKey);
        var message = (await subscription.ReceiveAsync(Visibility)).Single();
        await Assert.That(message.Id).IsEqualTo(id);
        await Assert.That(message.DeliveryCount).IsEqualTo(1);
        await Assert.That(await subscription.AckAsync(message)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DeniedAckPermissionPreventsDeadLetterAndInitialReadMutations(int protocol)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespireProtocol)protocol, AllowAdmin = true });
        var topic = Topic(admin, deliveryLimit: 1);
        var subscription = await topic.SubscribeAsync("permissions");
        await topic.PublishAsync("safe");
        var username = "topic-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">topic-password", "~*", "+@all", "-xack"]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Protocol = (RespireProtocol)protocol, Username = username, Password = "topic-password" });
            var other = await Topic(restricted, deliveryLimit: 1).SubscribeAsync("permissions");
            await Assert.That(async () => await other.ReceiveAsync(Visibility)).Throws<RespireServerException>();
            var message = (await subscription.ReceiveAsync(Visibility)).Single();
            await Assert.That(message.DeliveryCount).IsEqualTo(1);
            await Assert.That(async () => await other.AckAsync(message)).Throws<RespireServerException>();
            await subscription.NackAsync(message);
            await Assert.That(async () => await other.ReceiveAsync(Visibility)).Throws<RespireServerException>();
            await Assert.That(await admin.Streams.CountAsync(subscription.DeadLetterStreamKey)).IsEqualTo(0);
            await Assert.That((await subscription.ReceiveAsync(Visibility)).Length).IsEqualTo(0);
            await Assert.That(await admin.Streams.CountAsync(subscription.DeadLetterStreamKey)).IsEqualTo(1);
        }
        finally { using var deleted = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CodecFailureLeavesDeliveryRecoverableAndCancellationDoesNotPublish(int protocol)
    {
        await using var client = await Connect(protocol);
        var topic = Topic(client);
        var bad = new RespireReliableTopic<string>(client, "topic", Encoding.UTF8.GetBytes,
            _ => throw new InvalidOperationException("codec failure"));
        var badSubscription = await bad.SubscribeAsync("codec");
        var id = await topic.PublishAsync("recoverable");
        await Assert.That(async () => await badSubscription.ReceiveAsync(Visibility)).Throws<InvalidOperationException>();
        var subscription = await topic.SubscribeAsync("codec");
        await Assert.That(async () => await subscription.ReceiveAsync(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await subscription.ReceiveAsync(Visibility, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await subscription.SeekAsync(DateTimeOffset.FromUnixTimeMilliseconds(-1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That((await subscription.ReceiveAsync(Visibility)).Length).IsEqualTo(0);
        await MakeDue(client, subscription, id);
        var retry = (await subscription.ReceiveAsync(Visibility)).Single();
        await Assert.That(retry.Payload).IsEqualTo("recoverable");
        await Assert.That(retry.DeliveryCount).IsEqualTo(2);
        await Assert.That(async () => await subscription.NackAsync(retry, TimeSpan.FromMilliseconds(-1)))
            .Throws<ArgumentOutOfRangeException>();
        await subscription.AckAsync(retry);
        await Assert.That(async () => await topic.PublishAsync("cancelled", new(true))).Throws<OperationCanceledException>();
        await Assert.That(await client.Streams.CountAsync(topic.StreamKey)).IsEqualTo(1);
        await Assert.That(async () => await subscription.ReceiveAsync(Visibility, cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
    }

    private ValueTask<RespireClient> Connect(int protocol)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });

    private static RespireReliableTopic<string> Topic(IRespireClient client, int deliveryLimit = 5, int? maximumLength = null)
        => new(client, "topic", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, deliveryLimit, maximumLength);

    private static async Task MakeDue(IRespireClient client, RespireTopicSubscription<string> subscription, RespireStreamId id)
    {
        var state = subscription.DeadLetterStreamKey[..^5];
        using var result = await client.ExecuteAsync("ZADD", [state + ":due", 0, id.ToString()]);
    }
}
