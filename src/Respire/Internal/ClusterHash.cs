using System.Buffers;
using System.Text;

namespace Respire.Internal;

internal static class ClusterHash
{
    internal const int SlotCount = 16_384;
    private const int StackallocThreshold = 256;
    private static readonly ushort[] Crc16Table = CreateCrc16Table();

    internal static int GetSlot(string key)
    {
        var value = key.AsSpan();
        var open = value.IndexOf('{');
        if (open >= 0)
        {
            var tagged = value[(open + 1)..];
            var close = tagged.IndexOf('}');
            if (close > 0)
            {
                value = tagged[..close];
            }
        }

        ushort crc = 0;
        foreach (var character in value)
        {
            if (character > 0x7f)
            {
                return GetUtf8Slot(value);
            }

            crc = Update(crc, (byte)character);
        }

        return crc & (SlotCount - 1);
    }

    private static int GetUtf8Slot(ReadOnlySpan<char> key)
    {
        var byteCount = Encoding.UTF8.GetByteCount(key);
        byte[]? rented = null;
        var bytes = byteCount <= StackallocThreshold
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));

        try
        {
            Encoding.UTF8.GetBytes(key, bytes);
            return GetCrcSlot(bytes[..byteCount]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal static int GetSlot(ReadOnlySpan<byte> key)
    {
        var open = key.IndexOf((byte)'{');
        if (open >= 0)
        {
            var tagged = key[(open + 1)..];
            var close = tagged.IndexOf((byte)'}');
            if (close > 0)
            {
                key = tagged[..close];
            }
        }

        return GetCrcSlot(key);
    }

    private static int GetCrcSlot(ReadOnlySpan<byte> key)
    {
        ushort crc = 0;
        foreach (var value in key)
        {
            crc = Update(crc, value);
        }

        return crc & (SlotCount - 1);
    }

    private static ushort Update(ushort crc, byte value)
        => (ushort)((crc << 8) ^ Crc16Table[((crc >> 8) ^ value) & 0xff]);

    private static ushort[] CreateCrc16Table()
    {
        var table = new ushort[256];
        for (var value = 0; value < table.Length; value++)
        {
            var crc = (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }

            table[value] = crc;
        }

        return table;
    }
}
