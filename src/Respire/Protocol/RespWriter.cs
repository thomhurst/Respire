using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Respire.Networking;
using Respire.Internal;

namespace Respire.Protocol;

/// <summary>
/// Zero-allocation RESP command serializer writing directly into the connection's coalescing
/// write buffer. Obtained by the connection while it holds the write gate; commands implement
/// <see cref="IRespCommand"/> and append one complete RESP frame through this writer.
/// </summary>
internal ref struct RespWriter
{
    // Max digits for a long (19) + sign + type prefix + CRLF
    private const int MaxIntegerLineLength = 24;

    private readonly WriteBuffer _buffer;

    internal RespWriter(WriteBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>Writes "*&lt;count&gt;\r\n".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteArrayHeader(int count)
        => WritePrefixedLine(RespConstants.ArrayPrefix, count);

    /// <summary>Writes "$&lt;length&gt;\r\n&lt;value&gt;\r\n".</summary>
    public void WriteBulkString(scoped ReadOnlySpan<byte> value)
    {
        WritePrefixedLine(RespConstants.BulkStringPrefix, value.Length);
        var span = _buffer.GetSpan(value.Length + 2);
        value.CopyTo(span);
        span[value.Length] = RespConstants.CarriageReturn;
        span[value.Length + 1] = RespConstants.LineFeed;
        _buffer.Advance(value.Length + 2);
    }

    /// <summary>Writes a bulk string header without allocating or writing its payload.</summary>
    public void WriteBulkStringHeader(long length)
        => WritePrefixedLine(RespConstants.BulkStringPrefix, length);

    /// <summary>Writes the CRLF that terminates a bulk string payload.</summary>
    public void WriteBulkStringTerminator()
    {
        var span = _buffer.GetSpan(2);
        span[0] = RespConstants.CarriageReturn;
        span[1] = RespConstants.LineFeed;
        _buffer.Advance(2);
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
        var output = _buffer.GetSpan(checked(length + 2));
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(output.Slice(index * sizeof(float), sizeof(float)), values[index]);
        output[length] = RespConstants.CarriageReturn;
        output[length + 1] = RespConstants.LineFeed;
        _buffer.Advance(length + 2);
    }

    /// <summary>Writes a string as a bulk string, encoding UTF-8 directly into the buffer.</summary>
    public void WriteBulkString(string value)
    {
        if (value.Length == 0 || value[0] <= 0x7f)
        {
            var mark = _buffer.Count;
            WritePrefixedLine(RespConstants.BulkStringPrefix, value.Length);
            var asciiSpan = _buffer.GetSpan(value.Length + 2);
            if (Ascii.FromUtf16(value, asciiSpan, out var asciiBytes) == OperationStatus.Done)
            {
                asciiSpan[asciiBytes] = RespConstants.CarriageReturn;
                asciiSpan[asciiBytes + 1] = RespConstants.LineFeed;
                _buffer.Advance(asciiBytes + 2);
                return;
            }

            _buffer.TruncateTo(mark);
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        WritePrefixedLine(RespConstants.BulkStringPrefix, byteCount);
        var span = _buffer.GetSpan(byteCount + 2);
        Encoding.UTF8.GetBytes(value, span);

        span[byteCount] = RespConstants.CarriageReturn;
        span[byteCount + 1] = RespConstants.LineFeed;
        _buffer.Advance(byteCount + 2);
    }

    /// <summary>Writes an integer as a bulk string ("$3\r\n123\r\n") — how Redis expects numeric arguments.</summary>
    public void WriteBulkInteger(long value)
    {
        Span<byte> digits = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, digits, out var written);
        WriteBulkString(digits[..written]);
    }

    /// <summary>Appends pre-encoded RESP bytes (e.g. a pre-compiled command prefix) verbatim.</summary>
    public void WriteRaw(scoped ReadOnlySpan<byte> preEncoded)
    {
        var span = _buffer.GetSpan(preEncoded.Length);
        preEncoded.CopyTo(span);
        _buffer.Advance(preEncoded.Length);
    }

    private void WritePrefixedLine(byte prefix, long value)
    {
        var span = _buffer.GetSpan(MaxIntegerLineLength);
        span[0] = prefix;
        Utf8Formatter.TryFormat(value, span[1..], out var written);
        span[written + 1] = RespConstants.CarriageReturn;
        span[written + 2] = RespConstants.LineFeed;
        _buffer.Advance(written + 3);
    }
}

/// <summary>
/// A command that can serialize itself as a RESP frame. Implement on a readonly struct so the
/// connection's generic send path is fully monomorphized with no delegate or boxing overhead.
/// </summary>
internal interface IRespCommand
{
    void Write(ref RespWriter writer);

    void OnAccepted() { }

    /// <summary>Checks admission immediately before the frame and response slot are published.</summary>
    void ValidateAdmission() { }

    /// <summary>Separates a pre-submission budget from cancellation of an accepted response.</summary>
    CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => admissionToken;

    ReadCommandKind ReadKind { get; }

    int CursorArgumentIndex => -1;

    /// <summary>Returns cache mutation metadata for the command.</summary>
    RespireCacheMutation GetCacheMutation(string operation) => RespireCommands.GetCacheMutation(operation);

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
internal interface IRespCommandWrapper : IRespCommand
{
    new void ValidateAdmission();
    new CancellationToken GetResponseCancellationToken(CancellationToken admissionToken);
    new void OnAccepted();
}

/// <summary>A streamed command that can reset its source and resend after a Cluster redirect.</summary>
internal interface IReplayableStreamingRespCommand : IStreamingRespCommand
{
    bool CanReplay { get; }

    void ResetSourceForReplay();
}
