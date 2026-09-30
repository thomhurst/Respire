using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>Writes borrowed vector components directly into the RESP command buffer.</summary>
internal readonly struct VectorCommand(Verb verb, RespireValue key, ReadOnlyMemory<float> vector,
    RespireVectorEncoding encoding, RespireValue[] before, RespireValue[] after) : IRespCommand
{
    public bool TryGetPrimaryKey(out RespireValue primaryKey)
    {
        primaryKey = key;
        return true;
    }

    public bool TryGetClusterSlot(out int slot) => CommandRouting.TryGetClusterSlot(verb, 0, key, out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 1 + before.Length + after.Length +
            (encoding == RespireVectorEncoding.Fp32 ? 2 : 2 + vector.Length));
        writer.WriteRaw(verb.Bulk);
        key.WriteTo(ref writer);
        foreach (var argument in before) argument.WriteTo(ref writer);
        if (encoding == RespireVectorEncoding.Fp32)
        {
            writer.WriteBulkString("FP32"u8);
            writer.WriteBulkFloat32(vector.Span);
        }
        else
        {
            writer.WriteBulkString("VALUES"u8);
            writer.WriteBulkInteger(vector.Length);
            foreach (var component in vector.Span) ((RespireValue)component).WriteTo(ref writer);
        }
        foreach (var argument in after) argument.WriteTo(ref writer);
    }
}
