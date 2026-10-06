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

    /// <summary>Hashes a selected binary tag without interpreting any nested braces.</summary>
    internal static int GetTagSlot(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        ushort crc = 0;
        foreach (var value in first) crc = Update(crc, value);
        foreach (var value in second) crc = Update(crc, value);
        return crc & (SlotCount - 1);
    }

    /// <summary>Hashes a selected text tag, preserving UTF-16 pairs across the two segments.</summary>
    internal static int GetTagSlot(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        ushort crc = 0;
        foreach (var value in first)
        {
            if (value > 0x7f) return GetJoinedUtf8Slot(first, second);
            crc = Update(crc, (byte)value);
        }
        foreach (var value in second)
        {
            if (value > 0x7f) return GetJoinedUtf8Slot(first, second);
            crc = Update(crc, (byte)value);
        }
        return crc & (SlotCount - 1);
    }

    private static int GetJoinedUtf8Slot(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        var length = checked(first.Length + second.Length);
        char[]? rented = null;
        Span<char> tag = length <= StackallocThreshold ? stackalloc char[length]
            : (rented = ArrayPool<char>.Shared.Rent(length));
        try
        {
            first.CopyTo(tag);
            second.CopyTo(tag[first.Length..]);
            return GetUtf8Slot(tag[..length]);
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>Finds a nonempty first hash tag before any pattern metacharacter.</summary>
    internal static bool TryGetFixedPatternSlot(ReadOnlySpan<byte> pattern, out int slot)
    {
        slot = 0;
        var open = -1;
        for (var index = 0; index < pattern.Length; index++)
        {
            var value = pattern[index];
            if (value is (byte)'*' or (byte)'?' or (byte)'[' or (byte)'\\') return false;
            if (open < 0 && value == (byte)'{') open = index;
            else if (open >= 0 && value == (byte)'}')
            {
                if (index == open + 1) return false; // Redis hashes the whole key after an empty first tag.
                slot = GetCrcSlot(pattern[(open + 1)..index]);
                return true;
            }
        }
        return false;
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
