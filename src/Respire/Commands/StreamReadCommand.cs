using Respire.Protocol;

namespace Respire.Commands;

internal readonly struct StreamReadCommand(RespireValue[] keys, RespireStreamId[] ids, int? count, long? blockMilliseconds) : IRespCommand
{
    // Continuous readers update their private cursor array only after the preceding reply is owned.
    internal void SetAfter(int index, RespireStreamId id) => ids[index] = id;

    public bool TryGetPrimaryKey(out RespireValue key)
    {
        key = keys[0];
        return true;
    }

    public bool TryGetClusterSlot(out int slot) => keys[0].TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(2 + keys.Length * 2 + (count.HasValue ? 2 : 0) + (blockMilliseconds.HasValue ? 2 : 0));
        writer.WriteBulkString("XREAD"u8);
        if (count is { } take)
        {
            writer.WriteBulkString("COUNT"u8);
            writer.WriteBulkInteger(take);
        }
        if (blockMilliseconds is { } wait)
        {
            writer.WriteBulkString("BLOCK"u8);
            writer.WriteBulkInteger(wait);
        }
        writer.WriteBulkString("STREAMS"u8);
        foreach (var key in keys) key.WriteTo(ref writer);
        foreach (var id in ids) writer.WriteBulkString(id.Value);
    }
}
