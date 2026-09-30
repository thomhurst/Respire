using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Samples.Testing;

// These scenarios know only the public client options, not which server owns them.
internal static class SharedScenarios
{
    internal static async Task RunAsync(RespireOptions options)
    {
        await using var client = await RespireClient.ConnectAsync(options);
        await using var other = await RespireClient.ConnectAsync(options);
        await BinaryValueAsync(client);
        await HashAndBatchAsync(client);
        await WatchedTransactionAsync(client, other);
        await PublicationAsync(client, other);
    }

    private static async Task BinaryValueAsync(RespireClient client)
    {
        byte[] key = [0, 255, (byte)'k'];
        byte[] value = [0, 128, 255, 13, 10];
        using var stored = await client.ExecuteAsync(RespireCommands.String.SET, key, value);
        await Assert.That(stored.AsString()).IsEqualTo("OK");
        using var fetched = await client.ExecuteAsync(RespireCommands.String.GET, key);
        await Assert.That(fetched.AsBytes().AsSpan().SequenceEqual(value)).IsTrue();
    }

    private static async Task HashAndBatchAsync(RespireClient client)
    {
        await client.Hashes.SetAsync("user:1", "name", "Ada");
        await Assert.That(await client.Hashes.IncrementAsync("user:1", "visits")).IsEqualTo(1L);
        using var batch = client.CreateBatch();
        var stored = batch.Strings.Set("greeting", "hello");
        var read = batch.Strings.GetString("greeting");
        await batch.ExecuteAsync();
        await Assert.That(await stored).IsTrue();
        await Assert.That(await read).IsEqualTo("hello");
    }

    private static async Task WatchedTransactionAsync(RespireClient client, RespireClient other)
    {
        await client.SetAsync("version", "1");
        await using var transaction = await client.CreateTransactionAsync(["version"]);
        var pending = transaction.Strings.Set("result", "not committed");
        await other.SetAsync("version", "2");
        await Assert.That(await transaction.CommitAsync()).IsFalse();
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Aborted);
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    private static async Task PublicationAsync(RespireClient client, RespireClient other)
    {
        // SubscribeAsync waits for acknowledgement; no scheduling delay is needed.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = await client.SubscribeAsync("events", deadline.Token);
        await using var messages = subscription.GetAsyncEnumerator(deadline.Token);
        await Assert.That(await other.PublishAsync("events", "ready", deadline.Token)).IsEqualTo(1L);
        await Assert.That(await messages.MoveNextAsync()).IsTrue();
        await Assert.That(messages.Current.Text).IsEqualTo("ready");
    }
}
