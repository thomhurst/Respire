using System.IO.Compression;

namespace Respire.Compression;

/// <summary>Raw DEFLATE value compression using versioned Respire frames and algorithm ID 2.</summary>
public sealed class DeflateValueCodec : RespireValueCodec
{
    private readonly CompressionLevel _level;

    /// <summary>Creates a thread-safe codec using the specified compression level. The default is Fastest.</summary>
    public DeflateValueCodec(RespireValueCodecOptions? options = null, CompressionLevel level = CompressionLevel.Fastest)
        : base(2, options, allowReservedAlgorithm: true)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        _level = level;
    }

    /// <inheritdoc/>
    protected override byte[] Compress(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, _level, leaveOpen: true)) compressor.Write(payload);
        return output.ToArray();
    }

    /// <inheritdoc/>
    protected override unsafe bool TryCompress(ReadOnlySpan<byte> payload, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.IsEmpty) return false;
        fixed (byte* pointer = destination)
        {
            using var output = new BoundedWriteStream(pointer, destination.Length);
            using (var compressor = new DeflateStream(output, _level, leaveOpen: true)) compressor.Write(payload);
            if (output.Overflowed) return false;
            bytesWritten = (int)output.Length;
            return true;
        }
    }

    /// <inheritdoc/>
    protected override unsafe void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        if (payload.IsEmpty) throw new InvalidDataException("DEFLATE payload is empty.");
        fixed (byte* pointer = payload)
        {
            using var input = new UnmanagedMemoryStream(pointer, payload.Length);
            using var decoder = new DeflateStream(input, CompressionMode.Decompress);
            try
            {
                decoder.ReadExactly(destination);
                if (decoder.ReadByte() != -1)
                    throw new InvalidDataException("DEFLATE output exceeds its declared decoded length.");
            }
            catch (EndOfStreamException error)
            {
                throw new InvalidDataException("DEFLATE output is shorter than its declared decoded length.", error);
            }
        }
    }

    // Incompressible output is discarded without exceptions or allocating expansion storage.
    // The stream and compressor are disposed before the destination leaves its fixed scope.
    // Capacity and Position are long; compare the remaining capacity rather than adding to Position.
    internal sealed unsafe class BoundedWriteStream(byte* pointer, int capacity)
        : UnmanagedMemoryStream(pointer, 0, capacity, FileAccess.Write)
    {
        internal bool Overflowed { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Overflowed) return;
            if (buffer.Length > Capacity - Position)
            {
                Overflowed = true;
                return;
            }
            // For derived streams the runtime delegates this to Write(byte[], int, int).
            // Keep the guard here too, independently of that compatibility dispatch.
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Overflowed) return;
            if (count > Capacity - Position)
            {
                Overflowed = true;
                return;
            }
            base.Write(buffer, offset, count);
        }
    }
}
