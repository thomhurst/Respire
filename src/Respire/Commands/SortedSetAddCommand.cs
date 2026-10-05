using System.Numerics;
using Respire.Protocol;

namespace Respire.Commands;

// Keep the existing option-free Cmd3 fast path unchanged. A single member needs no argument array.
internal readonly struct SortedSetAddCommand(
    RespireValue key, RespireSortedSetAddOptions options, RespireValue member, double score,
    bool increment = false, RespireValue[]? pairs = null) : IRespCommand
{
    public ReadCommandKind ReadKind => ReadCommandKind.None;
    public bool TryGetPrimaryKey(out RespireValue primaryKey) { primaryKey = key; return true; }
    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        // Each validated option bit writes exactly one token below. Count verb/key, optional INCR,
        // and either the flattened score/member pairs or the single score/member pair.
        writer.WriteArrayHeader(2 + BitOperations.PopCount((uint)options) + (increment ? 1 : 0) + (pairs?.Length ?? 2));
        writer.WriteRaw(Verbs.ZAdd.Bulk);
        key.WriteTo(ref writer);
        if ((options & RespireSortedSetAddOptions.Nx) != 0) writer.WriteBulkString("NX"u8);
        if ((options & RespireSortedSetAddOptions.Xx) != 0) writer.WriteBulkString("XX"u8);
        if ((options & RespireSortedSetAddOptions.Gt) != 0) writer.WriteBulkString("GT"u8);
        if ((options & RespireSortedSetAddOptions.Lt) != 0) writer.WriteBulkString("LT"u8);
        if ((options & RespireSortedSetAddOptions.Ch) != 0) writer.WriteBulkString("CH"u8);
        if (increment) writer.WriteBulkString("INCR"u8);
        if (pairs is not null)
        {
            foreach (var value in pairs) value.WriteTo(ref writer);
        }
        else
        {
            RespireValue scoreValue = score;
            scoreValue.WriteTo(ref writer);
            member.WriteTo(ref writer);
        }
    }

    internal static void Validate(RespireSortedSetAddOptions options)
    {
        const RespireSortedSetAddOptions all = RespireSortedSetAddOptions.Nx | RespireSortedSetAddOptions.Xx
            | RespireSortedSetAddOptions.Gt | RespireSortedSetAddOptions.Lt | RespireSortedSetAddOptions.Ch;
        if ((options & ~all) != 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown ZADD option.");
        if ((options & RespireSortedSetAddOptions.Nx) != 0
            && (options & (RespireSortedSetAddOptions.Xx | RespireSortedSetAddOptions.Gt | RespireSortedSetAddOptions.Lt)) != 0)
            throw new ArgumentException("NX cannot be combined with XX, GT, or LT.", nameof(options));
        if ((options & (RespireSortedSetAddOptions.Gt | RespireSortedSetAddOptions.Lt))
            == (RespireSortedSetAddOptions.Gt | RespireSortedSetAddOptions.Lt))
            throw new ArgumentException("GT cannot be combined with LT.", nameof(options));
    }
}
