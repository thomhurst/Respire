using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Respire.Commands;
using Respire.Networking;
using Respire.Internal;

namespace Respire.Protocol;

/// <summary>
/// Zero-allocation RESP command serializer writing directly into the connection's coalescing
/// write buffer. Obtained by the connection while it holds the write gate; commands implement
/// <see cref="IRespCommand"/> and append one complete RESP frame through this writer.
/// </summary>
/// <remarks>
/// The cached span must not survive external buffer mutation or an await. Complete
/// synchronous serialization before exposing WrittenMemory or leaving the write gate.
/// Debug builds reject stale writers, unfinished-buffer consumption, and positive bounds smaller than the bytes published.
/// </remarks>
internal ref struct RespWriter
{
    // Max digits for a long (19) + sign + type prefix + CRLF
    internal const int MaxIntegerLineLength = 24;

    private readonly WriteBuffer _buffer;
    private readonly bool _allowGrowth;
    private Span<byte> _destination;
    private int _start;
    private int _position;
#if DEBUG
    private int _remainingReservation;
    private readonly long _writerSequence;
    private long _bufferVersion;
#endif

    internal RespWriter(WriteBuffer buffer, int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
#if DEBUG
        _writerSequence = buffer.BeginWriter();
#endif
        _buffer = buffer;
        _allowGrowth = sizeHint == 0;
        _start = buffer.Count;
        _destination = buffer.GetSpan(sizeHint);
        _position = 0;
#if DEBUG
        _remainingReservation = sizeHint;
        _bufferVersion = buffer.WriterMutationVersion;
#endif
    }

    /// <summary>Publishes bytes after synchronous serialization and any final admission check.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Complete()
    {
#if DEBUG
        _buffer.ValidateWriter(_writerSequence, _bufferVersion, publishing: true);
        if (!_allowGrowth)
        {
            if (_position > _remainingReservation)
                throw new InvalidOperationException("The RESP command exceeded its positive write size hint.");
            _remainingReservation -= _position;
        }
#endif
        _buffer.Advance(_position);
        _start += _position;
        _destination = _destination[_position..];
        _position = 0;
#if DEBUG
        _buffer.MarkWriterPublished();
        _bufferVersion = _buffer.WriterMutationVersion;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetSpan(int sizeHint)
    {
#if DEBUG
        _buffer.ValidateWriter(_writerSequence, _bufferVersion, publishing: false);
#endif
        // Known commands reserve their complete upper bound once. Unknown commands
        // preserve all unpublished bytes before replacing the backing array.
        if (_allowGrowth) EnsureCapacity(checked(_position + sizeHint));
#if DEBUG
        if (sizeHint > 0)
        {
            _buffer.MarkWriterUnpublished(_writerSequence);
        }
#endif
        return _destination[_position..];
    }

    private void EnsureCapacity(int required)
    {
        if (required > _destination.Length)
        {
            _destination = _buffer.GetSpanForRewrite(_start, _position, required);
#if DEBUG
            _bufferVersion = _buffer.WriterMutationVersion;
#endif
        }
    }

    /// <summary>Writes "*&lt;count&gt;\r\n".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteArrayHeader(int count)
    {
        if ((uint)(count - 1) < 9)
            WriteRaw("*1\r\n*2\r\n*3\r\n*4\r\n*5\r\n*6\r\n*7\r\n*8\r\n*9\r\n"u8.Slice((count - 1) * 4, 4));
        else
            WritePrefixedLine(RespConstants.ArrayPrefix, count);
    }

    /// <summary>Writes "$&lt;length&gt;\r\n&lt;value&gt;\r\n".</summary>
    public void WriteBulkString(scoped ReadOnlySpan<byte> value)
    {
        WritePrefixedLine(RespConstants.BulkStringPrefix, value.Length);
        var span = GetSpan(checked(value.Length + 2));
        value.CopyTo(span);
        span[value.Length] = RespConstants.CarriageReturn;
        span[value.Length + 1] = RespConstants.LineFeed;
        _position += value.Length + 2;
    }

    /// <summary>Writes a bulk string header without allocating or writing its payload.</summary>
    public void WriteBulkStringHeader(long length)
        => WritePrefixedLine(RespConstants.BulkStringPrefix, length);

    /// <summary>Writes the CRLF that terminates a bulk string payload.</summary>
    public void WriteBulkStringTerminator()
    {
        var span = GetSpan(2);
        span[0] = RespConstants.CarriageReturn;
        span[1] = RespConstants.LineFeed;
        _position += 2;
    }

    /// <summary>Writes a resolved key directly into the coalescing buffer without concatenating its storage.</summary>
    internal void WritePrefixedKey(KeyPrefix prefix, string? text, ReadOnlyMemory<byte> bytes)
    {
        if (text is null)
        {
            // Binary suffixes need neither UTF-8 length calculation nor surrogate handling.
            var binaryLength = checked(prefix.Bytes.Length + bytes.Length);
            WriteBulkStringHeader(binaryLength);
            var binaryPayload = GetSpan(checked(binaryLength + 2));
            prefix.Bytes.CopyTo(binaryPayload);
            bytes.Span.CopyTo(binaryPayload[prefix.Bytes.Length..]);
            binaryPayload[binaryLength] = RespConstants.CarriageReturn;
            binaryPayload[binaryLength + 1] = RespConstants.LineFeed;
            _position += binaryLength + 2;
            return;
        }
        if (text.Length == 0 || text[0] <= 0x7f)
        {
            // An ASCII first code unit cannot pair with a surrogate at the prefix boundary.
            var mark = _position;
            var asciiLength = checked(prefix.Bytes.Length + text.Length);
            WriteBulkStringHeader(asciiLength);
            var asciiPayload = GetSpan(checked(asciiLength + 2));
            prefix.Bytes.CopyTo(asciiPayload);
            if (Ascii.FromUtf16(text, asciiPayload[prefix.Bytes.Length..], out var asciiBytes) == OperationStatus.Done)
            {
                asciiPayload[asciiLength] = RespConstants.CarriageReturn;
                asciiPayload[asciiLength + 1] = RespConstants.LineFeed;
                _position += asciiLength + 2;
                return;
            }
            CompleteUtf8Suffix(mark, checked(prefix.Bytes.Length + asciiBytes), text.AsSpan(asciiBytes));
            return;
        }
        var length = prefix.GetWireLength(text, bytes);
        WriteBulkStringHeader(length);
        var payload = GetSpan(checked(length + 2));
        prefix.WritePayload(text, bytes, payload);
        payload[length] = RespConstants.CarriageReturn;
        payload[length + 1] = RespConstants.LineFeed;
        _position += length + 2;
    }

    /// <summary>Writes FP32 components in little-endian order without a temporary vector buffer.</summary>
    public void WriteBulkFloat32(scoped ReadOnlySpan<float> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            WriteBulkString(MemoryMarshal.AsBytes(values));
            return;
        }

        var length = checked(values.Length * sizeof(float));
        WritePrefixedLine(RespConstants.BulkStringPrefix, length);
        var output = GetSpan(checked(length + 2));
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(output.Slice(index * sizeof(float), sizeof(float)), values[index]);
        output[length] = RespConstants.CarriageReturn;
        output[length + 1] = RespConstants.LineFeed;
        _position += length + 2;
    }

    /// <summary>Writes a string as a bulk string, encoding UTF-8 directly into the buffer.</summary>
    public void WriteBulkString(string value)
    {
        if (value.Length == 0 || value[0] <= 0x7f)
        {
            var mark = _position;
            WritePrefixedLine(RespConstants.BulkStringPrefix, value.Length);
            var asciiSpan = GetSpan(checked(value.Length + 2));
            if (Ascii.FromUtf16(value, asciiSpan, out var asciiBytes) == OperationStatus.Done)
            {
                asciiSpan[asciiBytes] = RespConstants.CarriageReturn;
                asciiSpan[asciiBytes + 1] = RespConstants.LineFeed;
                _position += asciiBytes + 2;
                return;
            }

            CompleteUtf8Suffix(mark, asciiBytes, value.AsSpan(asciiBytes));
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        WritePrefixedLine(RespConstants.BulkStringPrefix, byteCount);
        var span = GetSpan(checked(byteCount + 2));
        Encoding.UTF8.GetBytes(value, span);

        span[byteCount] = RespConstants.CarriageReturn;
        span[byteCount + 1] = RespConstants.LineFeed;
        _position += byteCount + 2;
    }

    private void CompleteUtf8Suffix(int mark, int encodedPrefixLength, scoped ReadOnlySpan<char> suffix)
    {
        var oldHeaderLength = _position - mark;
        var byteCount = checked(encodedPrefixLength + Encoding.UTF8.GetByteCount(suffix));
        Span<byte> header = stackalloc byte[MaxIntegerLineLength];
        header[0] = RespConstants.BulkStringPrefix;
        Utf8Formatter.TryFormat(byteCount, header[1..], out var digits);
        var headerLength = digits + 3;
        header[digits + 1] = RespConstants.CarriageReturn;
        header[digits + 2] = RespConstants.LineFeed;
        var frameLength = checked(headerLength + byteCount + 2);

        _position += encodedPrefixLength;
        if (_allowGrowth) EnsureCapacity(checked(mark + frameLength));
        var frame = _destination.Slice(mark, frameLength);
        if (headerLength != oldHeaderLength)
            frame.Slice(oldHeaderLength, encodedPrefixLength).CopyTo(frame[headerLength..]);
        header[..headerLength].CopyTo(frame);
        Encoding.UTF8.GetBytes(suffix, frame[(headerLength + encodedPrefixLength)..]);
        frame[headerLength + byteCount] = RespConstants.CarriageReturn;
        frame[headerLength + byteCount + 1] = RespConstants.LineFeed;
        _position = mark + frameLength;
    }

    /// <summary>Writes an integer as a bulk string ("$3\r\n123\r\n") — how Redis expects numeric arguments.</summary>
    public void WriteBulkInteger(long value)
    {
        // Unsigned subtraction handles Int64.MinValue without signed overflow.
        var magnitude = value < 0 ? unchecked(0UL - (ulong)value) : (ulong)value;
        var length = (BitOperations.Log2(magnitude | 1) * 1233 >> 12) + 1;
        if (magnitude >= PowersOfTen[length]) length++;
        if (value < 0) length++;
        WriteBulkStringHeader(length);
        var payload = GetSpan(length + 2);
        Utf8Formatter.TryFormat(value, payload[..length], out _);
        payload[length] = RespConstants.CarriageReturn;
        payload[length + 1] = RespConstants.LineFeed;
        _position += length + 2;
    }

    // 1233/4096 approximates log10(2) from below. For a 64-bit magnitude the estimate
    // can be at most one decimal digit short; one power-of-ten comparison corrects it.
    // A cached array also avoids net8.0's per-access RuntimeFieldHandle allocation
    // from the generic RuntimeHelpers.CreateSpan<ulong> emitted for an RVA span.
    private static readonly ulong[] PowersOfTen =
    [
        1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000,
        100_000_000, 1_000_000_000, 10_000_000_000, 100_000_000_000,
        1_000_000_000_000, 10_000_000_000_000, 100_000_000_000_000,
        1_000_000_000_000_000, 10_000_000_000_000_000, 100_000_000_000_000_000,
        1_000_000_000_000_000_000, 10_000_000_000_000_000_000,
    ];

    /// <summary>Appends pre-encoded RESP bytes (e.g. a pre-compiled command prefix) verbatim.</summary>
    public void WriteRaw(scoped ReadOnlySpan<byte> preEncoded)
    {
        var span = GetSpan(preEncoded.Length);
        preEncoded.CopyTo(span);
        _position += preEncoded.Length;
    }

    private void WritePrefixedLine(byte prefix, long value)
    {
        var span = GetSpan(MaxIntegerLineLength);
        span[0] = prefix;
        Utf8Formatter.TryFormat(value, span[1..], out var written);
        span[written + 1] = RespConstants.CarriageReturn;
        span[written + 2] = RespConstants.LineFeed;
        _position += written + 3;
    }
}

/// <summary>
/// A command that can serialize itself as a RESP frame. Implement on a readonly struct so the
/// connection's generic send path is fully monomorphized with no delegate or boxing overhead.
/// </summary>
internal interface IRespCommand
{
    void Write(ref RespWriter writer);

    /// <summary>A checked complete-frame upper bound, or zero for the growing fallback.</summary>
    /// <remarks>A positive bound must cover the complete frame; it disables growth during serialization.
    /// Keep bounds tight without extra payload scans: bounds above the scratch retention limit select gate-held serialization.</remarks>
    int GetWriteSizeHint() => 0;

    void OnAccepted() { }

    /// <summary>Explicitly identifies connection-owned protocol setup, rather than application data dispatch.</summary>
    bool IsConnectionProtocol => false;

    /// <summary>Retains cache mutation ownership through native FIFO response retirement.</summary>
    ClientSideCacheCoordinator.MutationFence GetMutationFence() => default;

    /// <summary>Checks admission before redirect recovery and immediately before frame and response-slot publication.</summary>
    void ValidateAdmission() { }

    /// <summary>Separates a pre-submission budget from cancellation of an accepted response.</summary>
    CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => admissionToken;

    ReadCommandKind ReadKind { get; }

    int CursorArgumentIndex => -1;

    /// <summary>Returns cache mutation metadata for the command.</summary>
    /// <remarks>An empty operation requests classification from the wire command or an explicit declaration,
    /// without trusting a diagnostic name. Unclassified commands must remain unknown.</remarks>
    RespireCacheMutation GetCacheMutation(string operation) => RespireCommands.GetCacheMutation(operation);

    /// <summary>Returns precomputed cache classification, or conservatively classifies a custom command.</summary>
    ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => ClientCacheCommandMetadata.Get(operation);

    /// <summary>Returns the command's primary routing key when it is represented explicitly.</summary>
    bool TryGetPrimaryKey(out RespireValue key)
    {
        key = default;
        return false;
    }

    /// <summary>
    /// Supplies a Redis Cluster hash slot when the command has a routing key. Custom commands
    /// may override this using <see cref="RespireKey.ClusterSlot"/>; the default lets a cluster
    /// node redirect commands whose key is unknown.
    /// </summary>
    bool TryGetClusterSlot(out int slot)
    {
        slot = 0;
        return false;
    }

    /// <summary>Builds an invocation identity for server-assisted client-side caching.</summary>
    bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
    {
        key = default;
        return false;
    }

    /// <summary>
    /// Returns the argument at <paramref name="index"/> (counting after the command name) when the
    /// command keeps its arguments as a list. Read routing uses it to find a scan cursor.
    /// </summary>
    bool TryGetArgument(int index, out RespireValue value)
    {
        value = default;
        return false;
    }
}

/// <summary>
/// Marks a command whose payload the connection streams in bounded chunks instead of serializing
/// through <see cref="IRespCommand.Write"/>. The connection owns the write path for the whole
/// frame, so these commands cannot be batched. Replayable streamed commands can follow Cluster
/// redirects when they can reset their source without materializing the payload.
/// </summary>
internal interface IStreamingRespCommand : IRespCommand
{
}

/// <summary>
/// Requires wrappers to choose admission, response cancellation, and acceptance behavior explicitly.
/// Ordinary commands retain the defaults on <see cref="IRespCommand"/>.
/// </summary>
internal interface IRespCommandWrapper : IMutationAdmissionCommand
{
    new bool IsConnectionProtocol { get; }
    new RespireCacheMutation GetCacheMutation(string operation);
    new ClientCacheCommandMetadata GetClientCacheMetadata(string operation);
    new void ValidateAdmission();
    new CancellationToken GetResponseCancellationToken(CancellationToken admissionToken);
    new void OnAccepted();
}

/// <summary>Explicitly carries a mutation fence; ordinary commands need no boxed default call.</summary>
internal interface IMutationAdmissionCommand : IRespCommand
{
    new ClientSideCacheCoordinator.MutationFence GetMutationFence();
}

/// <summary>A streamed command that can reset its source and resend after a Cluster redirect.</summary>
internal interface IReplayableStreamingRespCommand : IStreamingRespCommand
{
    bool CanReplay { get; }

    void ResetSourceForReplay();
}
