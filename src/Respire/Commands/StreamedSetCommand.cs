using Respire.Protocol;

namespace Respire.Commands;

internal readonly struct StreamedSetCommand(
    RespireValue key, Stream source, long length, RespireExpiry expiry, SetWhen when) : IRespCommand
{
    internal RespireValue Key => key;
    internal Stream Source => source;
    internal long Length => length;

    public bool TryGetPrimaryKey(out RespireValue primaryKey)
    {
        primaryKey = key;
        return true;
    }

    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
        => throw new InvalidOperationException("Streamed SET commands must use the streaming connection path.");

    internal void WriteStart(ref RespWriter writer)
    {
        var argumentCount = 3 + expiry.TokenCount + (when == SetWhen.Always ? 0 : 1);
        writer.WriteArrayHeader(argumentCount);
        writer.WriteRaw(Verbs.Set.Bulk);
        key.WriteTo(ref writer);
        writer.WriteBulkStringHeader(length);
    }

    internal void WriteEnd(ref RespWriter writer)
    {
        writer.WriteBulkStringTerminator();
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

        if (when == SetWhen.NotExists) writer.WriteBulkString("NX"u8);
        else if (when == SetWhen.Exists) writer.WriteBulkString("XX"u8);
        if (expiry.IsKeep) writer.WriteBulkString("KEEPTTL"u8);
    }
}
