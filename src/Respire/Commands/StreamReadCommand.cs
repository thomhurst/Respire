using Respire.Protocol;

namespace Respire.Commands;

internal readonly struct StreamReadCommand(RespireValue[] keys, RespireStreamId[] ids, int? count, long? blockMilliseconds,
    long? maxCount = null, long? maxSize = null, string? group = null, string? consumer = null,
    bool noAck = false, long? claimMinIdleMilliseconds = null) : IRespCommand
{
    private static readonly ReadCommandKind ReadClassification = CommandReadMetadata.Get("XREAD").Kind;
    private static readonly ReadCommandKind GroupReadClassification = CommandReadMetadata.Get("XREADGROUP").Kind;
    public ReadCommandKind ReadKind => group is null ? ReadClassification : GroupReadClassification;

    // Sends are sequential. Cursors change only after a successful read's reply is fully owned;
    // failed/cancelled sends retry unchanged cursors and never mutate a still-borrowed command.
    internal void SetAfter(int index, RespireStreamId id) => ids[index] = id;

    public bool TryGetPrimaryKey(out RespireValue key)
    {
        key = keys[0];
        return true;
    }

    public bool TryGetClusterSlot(out int slot) => keys[0].TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(2 + keys.Length * 2 + (count.HasValue ? 2 : 0) + (blockMilliseconds.HasValue ? 2 : 0)
            + (maxCount.HasValue ? 2 : 0) + (maxSize.HasValue ? 2 : 0) + (group is null ? 0 : 3)
            + (noAck ? 1 : 0) + (claimMinIdleMilliseconds.HasValue ? 2 : 0));
        writer.WriteBulkString(group is null ? "XREAD"u8 : "XREADGROUP"u8);
        if (group is not null)
        {
            writer.WriteBulkString("GROUP"u8);
            writer.WriteBulkString(group);
            writer.WriteBulkString(consumer!);
        }
        if (count is { } take)
        {
            writer.WriteBulkString("COUNT"u8);
            writer.WriteBulkInteger(take);
        }
        if (maxCount is { } total)
        {
            writer.WriteBulkString("MAXCOUNT"u8);
            writer.WriteBulkInteger(total);
        }
        if (maxSize is { } bytes)
        {
            writer.WriteBulkString("MAXSIZE"u8);
            writer.WriteBulkInteger(bytes);
        }
        if (blockMilliseconds is { } wait)
        {
            writer.WriteBulkString("BLOCK"u8);
            writer.WriteBulkInteger(wait);
        }
        if (claimMinIdleMilliseconds is { } idle)
        {
            writer.WriteBulkString("CLAIM"u8);
            writer.WriteBulkInteger(idle);
        }
        if (noAck) writer.WriteBulkString("NOACK"u8);
        writer.WriteBulkString("STREAMS"u8);
        foreach (var key in keys) key.WriteTo(ref writer);
        foreach (var id in ids) writer.WriteBulkString(id.Value);
    }
}
