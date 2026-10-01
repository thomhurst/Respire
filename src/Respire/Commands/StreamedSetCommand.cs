using System.Buffers;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>
/// A SET whose value is streamed in bounded chunks from either a caller-owned <see cref="Stream"/>
/// or an in-memory <see cref="ReadOnlySequence{T}"/>. The connection writes the frame through its
/// streaming path (<see cref="IStreamingRespCommand"/>); <see cref="Write"/> is never used.
/// </summary>
internal readonly struct StreamedSetCommand : IStreamingRespCommand
{
    private readonly RespireValue _key;
    private readonly Stream? _stream;
    private readonly ReadOnlySequence<byte> _sequence;
    private readonly long _length;
    private readonly RespireExpiry _expiry;
    private readonly SetWhen _when;

    internal StreamedSetCommand(RespireValue key, Stream source, long length, RespireExpiry expiry, SetWhen when)
    {
        _key = key;
        _stream = source;
        _sequence = default;
        _length = length;
        _expiry = expiry;
        _when = when;
    }

    internal StreamedSetCommand(RespireValue key, ReadOnlySequence<byte> source, RespireExpiry expiry, SetWhen when)
    {
        _key = key;
        _stream = null;
        _sequence = source;
        _length = source.Length;
        _expiry = expiry;
        _when = when;
    }

    internal RespireValue Key => _key;
    internal long Length => _length;

    /// <summary>The caller's stream, or <see langword="null"/> when the payload is an in-memory sequence.</summary>
    internal Stream? SourceStream => _stream;

    /// <summary>The in-memory payload; only meaningful when <see cref="SourceStream"/> is <see langword="null"/>.</summary>
    internal ReadOnlySequence<byte> Sequence => _sequence;

    public bool TryGetPrimaryKey(out RespireValue primaryKey)
    {
        primaryKey = _key;
        return true;
    }

    public bool TryGetClusterSlot(out int slot) => _key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
        => throw new InvalidOperationException("Streamed SET commands must use the streaming connection path.");

    internal void WriteStart(ref RespWriter writer)
    {
        // Validate before writing so an argument count can never promise a token WriteEnd omits.
        SetCommand.ValidateWhen(_when);
        SetCommand.ValidateExpiry(_expiry);
        var argumentCount = 3 + _expiry.TokenCount + (_when == SetWhen.Always ? 0 : 1);
        writer.WriteArrayHeader(argumentCount);
        writer.WriteRaw(Verbs.Set.Bulk);
        _key.WriteTo(ref writer);
        writer.WriteBulkStringHeader(_length);
    }

    internal void WriteEnd(ref RespWriter writer)
    {
        writer.WriteBulkStringTerminator();
        if (_expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            writer.WriteBulkString("PX"u8);
            writer.WriteBulkInteger(milliseconds);
        }
        else if (_expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            writer.WriteBulkString("PXAT"u8);
            writer.WriteBulkInteger(unixMilliseconds);
        }

        if (_when == SetWhen.NotExists) writer.WriteBulkString("NX"u8);
        else if (_when == SetWhen.Exists) writer.WriteBulkString("XX"u8);
        if (_expiry.IsKeep) writer.WriteBulkString("KEEPTTL"u8);
    }
}
