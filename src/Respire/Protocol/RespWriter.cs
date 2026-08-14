using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Networking;

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
}
