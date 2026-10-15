using Respire;

namespace Respire.Samples.Recipes;

public sealed class Outbox(IRespireClient client)
{
    public const string StateKey = "{orders}:state";
    public const string StreamKey = "{orders}:outbox";
    public const string Group = "publishers";

    public async Task<RespireStreamId> SaveOrderAsync(string orderId, string state,
        string eventId, CancellationToken cancellationToken = default)
    {
        // This transaction is for business state stored in Redis, not an external SQL database.
        await using var transaction = client.CreateTransaction();
        var saved = transaction.Hashes.Set(StateKey, (orderId, (RespireValue)state));
        var added = transaction.Streams.Add(StreamKey,
            ("event-id", eventId), ("order-id", orderId), ("state", state));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _ = saved.Result;
        return added.Result;
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
