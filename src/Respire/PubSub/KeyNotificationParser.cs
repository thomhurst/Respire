namespace Respire;

internal static class KeyNotificationParser
{
    internal static bool TryParse(in RespireMessage message, out RespireKeyNotification result)
    {
        result = default;
        if (message.Kind != RespireMessageKind.Message) return false;
        var channel = message.Channel.Bytes;
        var span = channel.Span;
        var at = span.IndexOf((byte)'@');
        if (at < 0) return false;
        var kind = span[..at] switch
        {
            var name when name.SequenceEqual("__keyspace"u8) => RespireKeyNotificationKind.KeySpace,
            var name when name.SequenceEqual("__keyevent"u8) => RespireKeyNotificationKind.KeyEvent,
            var name when name.SequenceEqual("__subkeyspace"u8) => RespireKeyNotificationKind.SubKeySpace,
            var name when name.SequenceEqual("__subkeyevent"u8) => RespireKeyNotificationKind.SubKeyEvent,
            var name when name.SequenceEqual("__subkeyspaceitem"u8) => RespireKeyNotificationKind.SubKeySpaceItem,
            var name when name.SequenceEqual("__subkeyspaceevent"u8) => RespireKeyNotificationKind.SubKeySpaceEvent,
            _ => RespireKeyNotificationKind.Unknown,
        };
        if (kind == RespireKeyNotificationKind.Unknown) return false;
        var databaseStart = at + 1;
        var separator = span[databaseStart..].IndexOf("__:"u8);
        if (separator < 0 || !TryDecimal(span.Slice(databaseStart, separator), out var database)) return false;
        var tail = channel[(databaseStart + separator + 3)..];
        var payload = message.Payload;
        ReadOnlyMemory<byte> key;
        ReadOnlyMemory<byte> type;
        RespireSubKeyEnumerable subkeys = default;
        switch (kind)
        {
            case RespireKeyNotificationKind.KeySpace:
                key = tail;
                type = payload;
                break;
            case RespireKeyNotificationKind.KeyEvent:
                type = tail;
                key = payload;
                break;
            case RespireKeyNotificationKind.SubKeySpace:
                var pipe = payload.Span.IndexOf((byte)'|');
                if (pipe < 0 || !TrySubKeys(payload[(pipe + 1)..], out subkeys)) return false;
                key = tail;
                type = payload[..pipe];
                break;
            case RespireKeyNotificationKind.SubKeyEvent:
                if (!TryReadLength(payload.Span, out var length, out var start)) return false;
                var end = start + length;
                if (end >= payload.Length || payload.Span[end] != (byte)'|'
                    || !TrySubKeys(payload[(end + 1)..], out subkeys)) return false;
                key = payload.Slice(start, length);
                type = tail;
                break;
            case RespireKeyNotificationKind.SubKeySpaceItem:
                var newline = tail.Span.IndexOf((byte)'\n');
                if (newline < 0) return false;
                key = tail[..newline];
                type = payload;
                subkeys = new(tail[(newline + 1)..], 1, single: true);
                break;
            case RespireKeyNotificationKind.SubKeySpaceEvent:
                var eventEnd = tail.Span.IndexOf((byte)'|');
                if (eventEnd < 0 || !TrySubKeys(payload, out subkeys)) return false;
                type = tail[..eventEnd];
                key = tail[(eventEnd + 1)..];
                break;
            default:
                return false;
        }
        if (type.IsEmpty) return false;
        result = new(kind, database, key, type, message.Channel, payload, subkeys);
        return true;
    }

    private static bool TrySubKeys(ReadOnlyMemory<byte> data, out RespireSubKeyEnumerable result)
    {
        result = default;
        var remaining = data.Span;
        var count = 0;
        // Redis emits subkey layouts only with at least one subkey. A zero-length
        // subkey is represented by 0:, not by an empty list payload.
        while (true)
        {
            if (!TryReadLength(remaining, out var length, out var start)) return false;
            count++;
            remaining = remaining[(start + length)..];
            if (remaining.IsEmpty) break;
            if (remaining[0] != (byte)',') return false;
            remaining = remaining[1..];
        }
        result = new(data, count);
        return true;
    }

    internal static bool TryReadLength(ReadOnlySpan<byte> data, out int length, out int start)
    {
        length = 0;
        start = 0;
        var colon = data.IndexOf((byte)':');
        if (colon < 0 || !TryDecimal(data[..colon], out length)) return false;
        start = colon + 1;
        return length <= data.Length - start;
    }

    private static bool TryDecimal(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        if (digits.IsEmpty || digits.Length > 10 || digits.Length > 1 && digits[0] == (byte)'0') return false;
        foreach (var digit in digits)
        {
            var number = digit - (byte)'0';
            if ((uint)number > 9 || value > (int.MaxValue - number) / 10) return false;
            value = value * 10 + number;
        }
        return true;
    }
}
