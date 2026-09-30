using Respire.Protocol;

namespace Respire.Commands;

// Separate from SetCommand so the common Always/NX/XX path keeps its existing size and encoding.
internal readonly struct ConditionalSetCommand(RespireValue key, RespireValue value,
    RespireValueCondition condition, RespireExpiry expiry, bool returnOld) : IRespCommand
{
    public bool TryGetPrimaryKey(out RespireValue primaryKey)
    {
        primaryKey = key;
        return true;
    }

    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(5 + expiry.TokenCount + (returnOld ? 1 : 0));
        writer.WriteRaw(Verbs.Set.Bulk);
        key.WriteTo(ref writer);
        value.WriteTo(ref writer);
        writer.WriteBulkString(condition.Token.Span);
        condition.Operand.WriteTo(ref writer);
        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            writer.WriteBulkString("PX"u8);
            writer.WriteBulkInteger(milliseconds);
        }
        else if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            writer.WriteBulkString("PXAT"u8);
            writer.WriteBulkInteger(unixMilliseconds);
        }
        else if (expiry.IsKeep)
            writer.WriteBulkString("KEEPTTL"u8);
        if (returnOld) writer.WriteBulkString("GET"u8);
    }
}
