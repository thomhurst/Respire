using System.Buffers;
using System.Globalization;
using System.Text;

namespace Respire.Testing;

internal sealed record FakeReply(byte Prefix, object? Value)
{
    internal static readonly FakeReply Null = new((byte)'$', null);
    internal static readonly FakeReply NullArray = new((byte)'*', null);
    internal static readonly FakeReply Ok = Simple("OK");
    internal static FakeReply Bulk(byte[]? value) => value is null ? Null : new((byte)'$', value);
    internal static FakeReply Text(string value) => Bulk(Encoding.UTF8.GetBytes(value));
    internal static FakeReply Simple(string value) => new((byte)'+', value);
    internal static FakeReply Error(string value) => new((byte)'-', value.Replace('\r', ' ').Replace('\n', ' '));
    internal static FakeReply Integer(long value) => new((byte)':', value);
    internal static FakeReply Double(double value) => new((byte)',', value);
    internal static FakeReply Array(FakeReply[] values) => new((byte)'*', values);
    internal static FakeReply Set(FakeReply[] values) => new((byte)'~', values);
    internal static FakeReply Map(FakeReply[] pairs) => new((byte)'%', pairs);

    internal byte[] Encode(bool resp3)
    {
        var writer = new ArrayBufferWriter<byte>();
        Write(writer, resp3);
        return writer.WrittenSpan.ToArray();
    }

    private void Write(ArrayBufferWriter<byte> writer, bool resp3)
    {
        if (Prefix == '*' && Value is null)
        {
            writer.Write(resp3 ? "_\r\n"u8 : "*-1\r\n"u8);
        }
        else if (Value is FakeReply[] elements)
        {
            Line(writer, Prefix == '~' && !resp3 ? (byte)'*' : Prefix, (elements.Length / (Prefix == '%' ? 2 : 1)).ToString(CultureInfo.InvariantCulture));
            foreach (var element in elements) element.Write(writer, resp3);
        }
        else if (Value is double score)
        {
            var text = double.IsPositiveInfinity(score) ? "inf" : double.IsNegativeInfinity(score) ? "-inf" : score.ToString("R", CultureInfo.InvariantCulture);
            if (resp3) Line(writer, (byte)',', text);
            else Text(text).Write(writer, resp3: false);
        }
        else if (Prefix == '$')
        {
            if (Value is not byte[] bytes) { writer.Write(resp3 ? "_\r\n"u8 : "$-1\r\n"u8); return; }
            Line(writer, Prefix, bytes.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write(bytes);
            writer.Write("\r\n"u8);
        }
        else Line(writer, Prefix, Value is long number ? number.ToString(CultureInfo.InvariantCulture) : (string)Value!);
    }

    private static void Line(ArrayBufferWriter<byte> writer, byte prefix, string text)
    {
        writer.GetSpan(1)[0] = prefix;
        writer.Advance(1);
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Write("\r\n"u8);
    }
}
