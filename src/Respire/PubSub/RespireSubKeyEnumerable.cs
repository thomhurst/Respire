namespace Respire;

/// <summary>Validated binary subkey slices with an allocation-free struct enumerator.</summary>
public readonly struct RespireSubKeyEnumerable
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly bool _single;

    internal RespireSubKeyEnumerable(ReadOnlyMemory<byte> data, int count, bool single = false)
    {
        _data = data;
        Count = count;
        _single = single;
    }

    /// <summary>The number of subkeys, including empty names.</summary>
    public int Count { get; }
    /// <summary>Returns a struct enumerator over owned memory slices.</summary>
    public Enumerator GetEnumerator() => new(_data, Count, _single);
    /// <summary>The first subkey, or empty memory when none exists; use Count to distinguish empty names.</summary>
    public ReadOnlyMemory<byte> FirstOrDefault()
    {
        var enumerator = GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current : default;
    }
    /// <summary>Copies subkey slices to the destination without copying their bytes.</summary>
    public void CopyTo(Span<ReadOnlyMemory<byte>> destination)
    {
        if (destination.Length < Count) throw new ArgumentException("The destination is too short.", nameof(destination));
        var index = 0;
        foreach (var subkey in this) destination[index++] = subkey;
    }
    /// <summary>Allocates an array of slices; underlying message bytes remain shared and owned.</summary>
    public ReadOnlyMemory<byte>[] ToArray()
    {
        var result = new ReadOnlyMemory<byte>[Count];
        CopyTo(result);
        return result;
    }

    /// <summary>A forward-only subkey enumerator.</summary>
    public struct Enumerator
    {
        private ReadOnlyMemory<byte> _remaining;
        private int _count;
        private readonly bool _single;
        internal Enumerator(ReadOnlyMemory<byte> data, int count, bool single)
        {
            _remaining = data;
            _count = count;
            _single = single;
        }
        /// <summary>The current binary subkey.</summary>
        public ReadOnlyMemory<byte> Current { get; private set; }
        /// <summary>Advances to the next validated subkey.</summary>
        public bool MoveNext()
        {
            if (_count == 0) return false;
            _count--;
            if (_single) Current = _remaining;
            else
            {
                // The parser validates every length and separator before constructing this
                // enumerable, so iteration only decodes offsets in the validated payload.
                KeyNotificationParser.TryReadLength(_remaining.Span, out var length, out var start);
                Current = _remaining.Slice(start, length);
                _remaining = _count == 0 ? default : _remaining[(start + length + 1)..];
            }
            return true;
        }
    }
}
