using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// Bitmap commands queued on a <see cref="RespireBatch"/> or <see cref="RespireTransaction"/>.
/// Mirrors <see cref="IBitmapCommands"/>.
/// </summary>
public interface IBatchBitmapCommands
{
    /// <summary>The bit at an offset. Redis: GETBIT.</summary>
    RespirePending<bool> GetAsync(RespireKey key, long offset);

    /// <summary>Sets the bit at an offset and returns its previous value. Redis: SETBIT.</summary>
    RespirePending<bool> SetAsync(RespireKey key, long offset, bool value);

    /// <summary>Number of set bits. Redis: BITCOUNT.</summary>
    RespirePending<long> CountAsync(RespireKey key);

    /// <summary>Number of set bits within a range. Redis: BITCOUNT.</summary>
    RespirePending<long> CountAsync(RespireKey key, long start, long end, BitIndexUnit unit = BitIndexUnit.Byte);

    /// <summary>The first offset holding <paramref name="value"/>, or -1. Redis: BITPOS.</summary>
    RespirePending<long> PositionAsync(
        RespireKey key, bool value, long? start = null, long? end = null, BitIndexUnit unit = BitIndexUnit.Byte);

    /// <summary>Combines bitmaps into <paramref name="destination"/>; returns its byte length. Redis: BITOP.</summary>
    RespirePending<long> OperateAsync(
        BitOperation operation, RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys);

    /// <summary>Runs bit-field operations in order. Redis: BITFIELD.</summary>
    RespirePending<long?[]> FieldAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> operations);

    /// <summary>Runs read-only bit-field GETs. Redis: BITFIELD_RO.</summary>
    RespirePending<long?[]> FieldReadOnlyAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> gets);
}

internal sealed class BatchBitmapCommands(IPendingSink sink) : IBatchBitmapCommands
{
    public RespirePending<bool> GetAsync(RespireKey key, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return sink.Add<Cmd2, bool>(
            "GETBIT", new Cmd2(RespireCommands.Bitmap.GETBIT.Verb, sink.Client.Key(in key), offset),
            static (c, v) => ResponseReader.Flag(in v));
    }

    public RespirePending<bool> SetAsync(RespireKey key, long offset, bool value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return sink.Add<Cmd3, bool>(
            "SETBIT", new Cmd3(RespireCommands.Bitmap.SETBIT.Verb, sink.Client.Key(in key), offset, value),
            static (c, v) => ResponseReader.Flag(in v));
    }

    public RespirePending<long> CountAsync(RespireKey key)
        => sink.Add<Cmd1, long>(
            "BITCOUNT", new Cmd1(RespireCommands.Bitmap.BITCOUNT.Verb, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> CountAsync(
        RespireKey key, long start, long end, BitIndexUnit unit = BitIndexUnit.Byte)
        => sink.Add<Cmd4, long>(
            "BITCOUNT",
            new Cmd4(
                RespireCommands.Bitmap.BITCOUNT.Verb, sink.Client.Key(in key), start, end, BitmapCommands.Unit(unit)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> PositionAsync(
        RespireKey key, bool value, long? start = null, long? end = null, BitIndexUnit unit = BitIndexUnit.Byte)
    {
        BitmapCommands.ValidatePosition(start, end, unit);
        return (start, end) switch
        {
            (null, _) => sink.Add<Cmd2, long>(
                "BITPOS", new Cmd2(RespireCommands.Bitmap.BITPOS.Verb, sink.Client.Key(in key), value),
                static (c, v) => ResponseReader.Integer(in v)),
            ({ } from, null) => sink.Add<Cmd3, long>(
                "BITPOS", new Cmd3(RespireCommands.Bitmap.BITPOS.Verb, sink.Client.Key(in key), value, from),
                static (c, v) => ResponseReader.Integer(in v)),
            ({ } from, { } to) => sink.Add<Cmd5, long>(
                "BITPOS",
                new Cmd5(
                    RespireCommands.Bitmap.BITPOS.Verb, sink.Client.Key(in key), value, from, to, BitmapCommands.Unit(unit)),
                static (c, v) => ResponseReader.Integer(in v)),
        };
    }

    public RespirePending<long> OperateAsync(
        BitOperation operation, RespireKey destination, params ReadOnlySpan<RespireKey> sourceKeys)
    {
        BitmapCommands.ValidateOperate(operation, sourceKeys);
        return sink.Add<BitOpCommand, long>(
            "BITOP",
            new BitOpCommand(
                RespireCommands.Bitmap.BITOP.Verb,
                BitmapCommands.Operation(operation),
                sink.Client.Key(in destination),
                sink.Client.MapKeys(sourceKeys)),
            destination, sourceKeys,
            static (c, v) => ResponseReader.Integer(in v));
    }

    public RespirePending<long?[]> FieldAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> operations)
        => FieldCore(RespireCommands.Bitmap.BITFIELD, "BITFIELD", key, operations, readOnly: false);

    public RespirePending<long?[]> FieldReadOnlyAsync(RespireKey key, params ReadOnlySpan<BitFieldOperation> gets)
        => FieldCore(RespireCommands.Bitmap.BITFIELD_RO, "BITFIELD_RO", key, gets, readOnly: true);

    private RespirePending<long?[]> FieldCore(
        RespireCommand command, string operation, RespireKey key,
        ReadOnlySpan<BitFieldOperation> operations, bool readOnly)
    {
        BitmapCommands.ValidateFieldOperations(operations, readOnly);
        return sink.Add<BitFieldCommand, long?[]>(
            operation,
            new BitFieldCommand(command.Verb, sink.Client.Key(in key), operations.ToArray()),
            static (c, v) => ResponseReader.NullableIntegerArray(in v));
    }
}
