using System.Buffers;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>
/// A SET whose value is streamed in bounded chunks from either a caller-owned <see cref="Stream"/>
/// or an in-memory <see cref="ReadOnlySequence{T}"/>. The connection writes the frame through its
/// streaming path (<see cref="IStreamingRespCommand"/>); <see cref="Write"/> is never used.
/// </summary>
internal readonly struct StreamedSetCommand : IReplayableStreamingRespCommand
{
    private sealed class StreamSource
    {
        private readonly Stream _original;
        private readonly long _originalPosition;

        internal StreamSource(Stream current)
        {
            _original = current;
            Current = current;
            if (!current.CanSeek) return;
            try
            {
                _originalPosition = current.Position;
                CanReplay = true;
            }
            catch (Exception error) when (error is NotSupportedException or IOException)
            {
                CanReplay = false;
            }
        }

        internal Stream Current { get; private set; }
        internal bool CanReplay { get; }

        internal void RestorePrefix(ReadOnlySpan<byte> prefix)
            => Current = new PrefixStream(prefix.ToArray(), Current);

        internal void ResetForReplay()
        {
            if (!CanReplay) throw new InvalidOperationException("This SET stream cannot be replayed.");
            _original.Position = _originalPosition;
            Current = _original;
        }
    }

    private sealed class PrefixStream(byte[] prefix, Stream remainder) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(Span<byte> buffer)
        {
            var copied = Math.Min(buffer.Length, prefix.Length - _offset);
            if (copied != 0)
            {
                prefix.AsSpan(_offset, copied).CopyTo(buffer);
                _offset += copied;
                return copied;
            }
            return remainder.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = Math.Min(buffer.Length, prefix.Length - _offset);
            if (copied != 0)
            {
                prefix.AsMemory(_offset, copied).CopyTo(buffer);
                _offset += copied;
                return ValueTask.FromResult(copied);
            }
            return remainder.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private readonly RespireValue _key;
    private readonly StreamSource? _stream;
    private readonly ReadOnlySequence<byte> _sequence;
    private readonly long _length;
    private readonly RespireExpiry _expiry;
    private readonly SetWhen _when;

    internal StreamedSetCommand(RespireValue key, Stream source, long length, RespireExpiry expiry, SetWhen when)
    {
        _key = key;
        _stream = new StreamSource(source);
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
    public bool CanReplay => _stream?.CanReplay ?? true;
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    /// <summary>The stream source, or <see langword="null"/> for an in-memory sequence.</summary>
    internal Stream? SourceStream => _stream?.Current;

    /// <summary>Put a consumed first chunk back for a replacement connection retry.</summary>
    internal void RestoreSourcePrefixForRetry(ReadOnlySpan<byte> prefix) => _stream?.RestorePrefix(prefix);

    public void ResetSourceForReplay() => _stream?.ResetForReplay();

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
