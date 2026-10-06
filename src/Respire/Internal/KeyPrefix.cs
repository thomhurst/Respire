using System.Text;

namespace Respire.Internal;

/// <summary>Immutable prefix encoding shared by every key resolved through a client view.</summary>
internal sealed class KeyPrefix
{
    internal string Text { get; }
    internal byte[] Bytes { get; }
    internal bool SnapshotBinaryKeys { get; }
    private readonly int _tagStart;
    private readonly int _binaryTagStart;
    private readonly int _fixedSlot;

    internal KeyPrefix(string text) : this(text, Encoding.UTF8.GetBytes(text), false) { }

    private KeyPrefix(string text, byte[] bytes, bool snapshotBinaryKeys)
    {
        Text = text;
        Bytes = bytes;
        SnapshotBinaryKeys = snapshotBinaryKeys;
        _tagStart = text.IndexOf('{');
        _binaryTagStart = bytes.AsSpan().IndexOf((byte)'{');
        var close = _tagStart < 0 ? -1 : text.AsSpan(_tagStart + 1).IndexOf('}');
        _fixedSlot = close > 0 ? ClusterHash.GetSlot(text) : -1;
        // An empty first tag disables tag selection, including later tags in the suffix.
        if (close == 0) _tagStart = _binaryTagStart = -2;
    }

    /// <summary>Shares the encoding while restoring owned binary keys for deferred batches.</summary>
    internal KeyPrefix ForDeferredBatch() => SnapshotBinaryKeys ? this : new(Text, Bytes, true);

    /// <summary>Materializes the exact wire bytes for an explicitly owned representation.</summary>
    internal byte[] Materialize(string? key, ReadOnlyMemory<byte> bytes)
    {
        var payload = new byte[GetWireLength(key, bytes)];
        WritePayload(key, bytes, payload);
        return payload;
    }

    /// <summary>Preserves text identity and uses the shared decoder for binary keys.</summary>
    internal string GetString(string? key, ReadOnlyMemory<byte> bytes)
        => key is not null ? Text + key : Utf8String.GetString(Materialize(null, bytes).AsMemory());

    /// <summary>Hashes only a nonempty first tag, including a tag split across the prefix boundary.</summary>
    internal bool TryGetTaggedSlot(string? key, ReadOnlyMemory<byte> bytes, out int slot)
    {
        slot = _fixedSlot;
        if (slot >= 0) return true;
        if (_tagStart == -2) return false;
        if (key is not null)
        {
            var suffix = key.AsSpan();
            if (_tagStart >= 0)
            {
                var close = suffix.IndexOf('}');
                var prefixTag = Text.AsSpan(_tagStart + 1);
                if (close < 0 || prefixTag.IsEmpty && close == 0) return false;
                slot = ClusterHash.GetTagSlot(prefixTag, suffix[..close]);
            }
            else
            {
                var open = suffix.IndexOf('{');
                if (open < 0) return false;
                var tag = suffix[(open + 1)..];
                var close = tag.IndexOf('}');
                if (close <= 0) return false;
                slot = ClusterHash.GetTagSlot(tag[..close], default);
            }
        }
        else
        {
            var suffix = bytes.Span;
            if (_binaryTagStart >= 0)
            {
                var close = suffix.IndexOf((byte)'}');
                var prefixTag = Bytes.AsSpan(_binaryTagStart + 1);
                if (close < 0 || prefixTag.IsEmpty && close == 0) return false;
                slot = ClusterHash.GetTagSlot(prefixTag, suffix[..close]);
            }
            else
            {
                var open = suffix.IndexOf((byte)'{');
                if (open < 0) return false;
                var tag = suffix[(open + 1)..];
                var close = tag.IndexOf((byte)'}');
                if (close <= 0) return false;
                slot = ClusterHash.GetTagSlot(tag[..close], default);
            }
        }
        return true;
    }

    private bool JoinsSurrogatePair(string? key)
        => Text.Length != 0 && char.IsHighSurrogate(Text[^1])
            && key is { Length: > 0 } && char.IsLowSurrogate(key[0]);

    internal int GetWireLength(string? key, ReadOnlyMemory<byte> bytes)
    {
        if (key is null) return checked(Bytes.Length + bytes.Length);
        if (JoinsSurrogatePair(key))
            return checked(Bytes.Length + 1 + Encoding.UTF8.GetByteCount(key.AsSpan(1)));
        return checked(Bytes.Length + Encoding.UTF8.GetByteCount(key));
    }

    internal int WritePayload(string? key, ReadOnlyMemory<byte> bytes, Span<byte> destination)
    {
        if (JoinsSurrogatePair(key))
        {
            // String concatenation pairs these UTF-16 code units before UTF-8 encoding.
            // Replace the cached trailing replacement character with the complete pair.
            var prefixLength = Bytes.Length - 3;
            Bytes.AsSpan(0, prefixLength).CopyTo(destination);
            Span<char> pair = stackalloc char[2] { Text[^1], key![0] };
            var written = Encoding.UTF8.GetBytes(pair, destination[prefixLength..]);
            return prefixLength + written + Encoding.UTF8.GetBytes(key.AsSpan(1), destination[(prefixLength + written)..]);
        }

        Bytes.CopyTo(destination);
        if (key is not null)
            return Bytes.Length + Encoding.UTF8.GetBytes(key, destination[Bytes.Length..]);
        bytes.Span.CopyTo(destination[Bytes.Length..]);
        return Bytes.Length + bytes.Length;
    }
}
