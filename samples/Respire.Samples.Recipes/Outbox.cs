using Respire;
using Respire.Streaming;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Respire.Samples.Recipes;

public sealed class Outbox(IRespireClient client)
{
    public const string StateKey = "{respire-job:orders}:state";
    public const string StreamKey = "{respire-job:orders}:ready";
    public const string Group = "publishers";
    private readonly RespireJobQueue<OrderEvent> _queue = new(client, "orders",
        payload => JsonSerializer.SerializeToUtf8Bytes(payload, OutboxJson.Default.OrderEvent));

    public async Task<RespireStreamId> SaveOrderAsync(string orderId, string state,
        string eventId, CancellationToken cancellationToken = default)
    {
        // This transaction is for business state stored in Redis, not an external SQL database.
        await using var transaction = client.CreateTransaction();
        var saved = transaction.Hashes.Set(StateKey, (orderId, (RespireValue)state));
        var added = _queue.Enqueue(transaction, eventId, new OrderEvent(orderId, state));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _ = saved.Result;
        return added.Result.EntryId;
    }

    public ValueTask<bool> InitializeRelayAsync(CancellationToken cancellationToken = default)
        => client.Streams.CreateGroupAsync(StreamKey, Group, "0-0", cancellationToken: cancellationToken);

    public async Task<int> RelayPageAsync(string consumer, TimeSpan claimMinIdle,
        Func<RespireStreamEntry, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default)
    {
        // Redis 8.4: recover abandoned deliveries and read new entries in the same command.
        var entries = await client.Streams.ReadGroupOnceAsync(StreamKey, Group, consumer,
            new RespireStreamReadOptions { Count = 10, ClaimMinIdle = claimMinIdle },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            await publish(entry, cancellationToken).ConfigureAwait(false);
            await entry.AckAsync(cancellationToken).ConfigureAwait(false); // Only acknowledge confirmed publication.
        }
        return entries.Length;
    }
}

public sealed record OrderEvent(string OrderId, string State);

[JsonSerializable(typeof(OrderEvent))]
internal partial class OutboxJson : JsonSerializerContext;
