using System.Buffers;
using System.Text;

namespace Respire.Internal;

internal static class ClusterHash
{
    internal const int SlotCount = 16_384;
    private const int StackallocThreshold = 256;
    private const int RemovalLeaseTagLength = 2;
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

    internal static void WriteRemovalLeaseTag(int slot, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, SlotCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, RemovalLeaseTagLength);

        var tag = RemovalLeaseTags.BySlot[slot];
        destination[0] = (byte)(tag >> 8);
        destination[1] = (byte)tag;
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

    private static class RemovalLeaseTags
    {
        internal static readonly ushort[] BySlot = Create();

        private static ushort[] Create()
        {
            var tags = new ushort[SlotCount];
            Array.Fill(tags, ushort.MaxValue);
            var remaining = SlotCount;
            Span<byte> candidateBytes = stackalloc byte[RemovalLeaseTagLength];
            for (var candidate = 0; candidate < ushort.MaxValue && remaining > 0; candidate++)
            {
                candidateBytes[0] = (byte)(candidate >> 8);
                candidateBytes[1] = (byte)candidate;
                if (candidateBytes.Contains((byte)'}'))
                {
                    continue;
                }

                var slot = GetCrcSlot(candidateBytes);
                if (tags[slot] == ushort.MaxValue)
                {
                    tags[slot] = (ushort)candidate;
                    remaining--;
                }
            }

            if (remaining != 0)
            {
                throw new InvalidOperationException("Unable to generate a binary hash tag for every Redis Cluster slot.");
            }

            return tags;
        }
    }
}
