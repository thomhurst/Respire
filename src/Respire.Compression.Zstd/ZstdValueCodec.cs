using System.Buffers;
using Reservoir;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Respire.Compression.Zstd;

/// <summary>Zstandard compression using versioned Respire frames and reserved algorithm ID 4.</summary>
/// <remarks>Requires the optional Respire.Compression.Zstd package. Each compressed payload is one
/// ordinary Zstandard frame without an external dictionary. Bounded shared pools reuse contexts exclusively
/// per call, so the codec supports concurrent calls and does not require disposal.</remarks>
public sealed class ZstdValueCodec : RespireValueCodec
{
    private readonly int _level;
    // No thread-local retention or per-level pools: arbitrary codec instances and levels share this bound.
    private static readonly ObjectPool<Compressor, CompressorPolicy> Compressors = new(Math.Min(Environment.ProcessorCount, 8));
    private static readonly ObjectPool<Decompressor, DecompressorPolicy> Decompressors = new(Math.Min(Environment.ProcessorCount, 8));
    private const int MaximumRetainedInputLength = 64 * 1024;
    private static ReadOnlySpan<byte> FrameMagic => [0x28, 0xb5, 0x2f, 0xfd];

    /// <summary>Creates a codec with compression level 3 by default. Supported levels are -131072 through 22;
    /// zero selects the dependency's default level of 3. Negative levels favor speed over compression ratio.</summary>
    public ZstdValueCodec(RespireValueCodecOptions? options = null, int level = 3)
        : base(4, options, allowReservedAlgorithm: true)
    {
        if (level < Compressor.MinCompressionLevel || level > Compressor.MaxCompressionLevel)
            throw new ArgumentOutOfRangeException(nameof(level), "Zstandard compression level is outside the supported range.");
        _level = level;
    }

    /// <inheritdoc/>
    protected override byte[] Compress(ReadOnlySpan<byte> payload)
    {
        var capacity = Compressor.GetCompressBound(payload.Length);
        if (capacity <= 0) throw new InvalidDataException("Zstandard input exceeds its supported frame size.");
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            if (!TryCompress(payload, buffer.AsSpan(0, capacity), out var written))
                throw new InvalidDataException("Zstandard could not encode the value within its output bound.");
            return buffer.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <inheritdoc/>
    protected override bool TryCompress(ReadOnlySpan<byte> payload, Span<byte> destination, out int bytesWritten)
    {
        // Large values bypass the pool so they do not displace reusable small-value workspaces.
        var compressor = RentContext(Compressors, payload.Length);
        var reusable = false;
        try
        {
            // Settings belong to the codec, not the previous renter. No context loads a dictionary.
            compressor.Level = _level;
            var fits = compressor.TryWrap(payload, destination, out bytesWritten);
            reusable = true;
            return fits;
        }
        finally
        {
            ReleaseContext(Compressors, compressor, payload.Length, reusable);
        }
    }

    /// <inheritdoc/>
    protected override void Decompress(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        // The base validates the outer decoded length against MaximumDecodedLength before
        // allocating this exact-sized destination. Unwrap remains bounded by that span.
        if (!IsSingleFrame(payload))
            throw new InvalidDataException("Zstandard payload must contain exactly one ordinary frame.");
        var decompressor = RentContext(Decompressors, destination.Length);
        var reusable = false;
        try
        {
            if (decompressor.Unwrap(payload, destination) != destination.Length)
                throw new InvalidDataException("Zstandard payload has an unexpected decoded length.");
            reusable = true;
        }
        catch (ZstdException error)
        {
            throw new InvalidDataException("Zstandard payload is malformed or exceeds its declared length.", error);
        }
        finally
        {
            ReleaseContext(Decompressors, decompressor, destination.Length, reusable);
        }
    }

    private static T RentContext<T, TPolicy>(ObjectPool<T, TPolicy> pool, int length)
        where T : class
        where TPolicy : struct, IPooledObjectPolicy<T>
        => length <= MaximumRetainedInputLength ? pool.Rent() : default(TPolicy).Create();

    private static void ReleaseContext<T, TPolicy>(ObjectPool<T, TPolicy> pool, T context, int length, bool reusable)
        where T : class, IDisposable
        where TPolicy : struct, IPooledObjectPolicy<T>
    {
        // Both directions share the retention rule. Failure and large workspaces never enter the pool.
        if (reusable && length <= MaximumRetainedInputLength) pool.Return(context);
        else context.Dispose();
    }

    internal readonly struct CompressorPolicy : IPooledObjectPolicy<Compressor>
    {
        public Compressor Create() => new();
        public bool TryReset(Compressor context)
        {
            context.ResetStream();
            return true;
        }
        public void Destroy(Compressor context) => context.Dispose();
    }

    internal readonly struct DecompressorPolicy : IPooledObjectPolicy<Decompressor>
    {
        public Decompressor Create() => new();
        public bool TryReset(Decompressor context)
        {
            context.ResetStream();
            return true;
        }
        public void Destroy(Decompressor context) => context.Dispose();
    }

    private static unsafe bool IsSingleFrame(ReadOnlySpan<byte> payload)
    {
        // The dependency's decoder also accepts concatenated and skippable frames. Our wire ID
        // deliberately denotes one ordinary frame, so reject those before bounded decoding.
        if (!payload.StartsWith(FrameMagic)) return false;
        fixed (byte* source = payload)
            return Methods.ZSTD_findFrameCompressedSize(source, (nuint)payload.Length) == (nuint)payload.Length;
    }
}
