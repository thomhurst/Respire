using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>Cheap checked bounds for the shared manual and generated command shapes.</summary>
internal static class CommandWriteSizeHint
{
    internal const int HeaderLength = RespWriter.MaxIntegerLineLength;
    internal static readonly int ExpiryOptions = CommandOptionFrames.PXAT.Length + Bulk(20)
        + CommandOptionFrames.KEEPTTL.Length;
    internal static readonly int SetOptions = ExpiryOptions + CommandOptionFrames.NX.Length + CommandOptionFrames.GET.Length;

    internal static int For(Verb verb, int first = 0, int second = 0,
        int third = 0, int fourth = 0, int fifth = 0)
        => For(checked(HeaderLength + verb.Bulk.Length), first, second, third, fourth, fifth);

    internal static int For(int prefixLength, int first = 0, int second = 0,
        int third = 0, int fourth = 0, int fifth = 0)
    {
        if (first < 0 || second < 0 || third < 0 || fourth < 0 || fifth < 0) return 0;
        return checked(prefixLength + first + second + third + fourth + fifth);
    }

    internal static int For(Verb verb, ReadOnlySpan<RespireValue> arguments)
        => Add(For(verb), arguments);

    internal static int Add(int prefixLength, ReadOnlySpan<RespireValue> arguments)
    {
        if (prefixLength == 0) return 0;
        foreach (var argument in arguments)
        {
            var length = argument.GetWriteSizeHint();
            if (length < 0) return 0;
            prefixLength = checked(prefixLength + length);
        }
        return prefixLength;
    }

    internal static int Bulk(int payloadLength) => checked(HeaderLength + payloadLength + 2);

    internal static int Combine(int first, int second)
        => first == 0 || second == 0 ? 0 : checked(first + second);
}
