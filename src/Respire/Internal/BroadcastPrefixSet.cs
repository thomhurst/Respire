using System.Collections;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>Owned, non-overlapping prefixes with one validated binary-search index.</summary>
internal sealed class BroadcastPrefixSet : IReadOnlyList<RespireKey>
{
    internal static readonly BroadcastPrefixSet Empty = new([], []);
    private readonly RespireKey[] _wirePrefixes;
    private readonly ReadOnlyMemory<byte>[] _sortedPrefixes;

    private BroadcastPrefixSet(RespireKey[] wirePrefixes, ReadOnlyMemory<byte>[] sortedPrefixes)
        => (_wirePrefixes, _sortedPrefixes) = (wirePrefixes, sortedPrefixes);

    internal static BroadcastPrefixSet Create(IReadOnlyList<RespireKey> prefixes)
    {
        if (prefixes is BroadcastPrefixSet owned) return owned;
        if (prefixes.Count == 0) return Empty;
        var keys = new RespireKey[prefixes.Count];
        var sorted = new ReadOnlyMemory<byte>[prefixes.Count];
        for (var index = 0; index < keys.Length; index++)
        {
            var argument = prefixes[index].AsValue();
            var bytes = new byte[argument.GetWireLength()];
            argument.WriteWirePayload(bytes);
            keys[index] = new RespireKey(bytes);
            sorted[index] = bytes;
        }
        Array.Sort(sorted, static (left, right) => left.Span.SequenceCompareTo(right.Span));
        for (var index = 1; index < sorted.Length; index++)
        {
            if (sorted[index].Span.StartsWith(sorted[index - 1].Span))
                throw new RespireConfigurationException("ClientSideCache.BroadcastPrefixes must not overlap or repeat.");
        }
        // Preserve caller order on the wire; both arrays refer to the same owned bytes.
        return new BroadcastPrefixSet(keys, sorted);
    }

    internal bool Contains(in RespireKey key) => Count == 0 || key.StartsWithAny(this);

    internal bool Matches(ReadOnlySpan<byte> key)
    {
        var low = 0;
        var high = _sortedPrefixes.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var prefix = _sortedPrefixes[middle].Span;
            if (key.StartsWith(prefix)) return true;
            if (key.SequenceCompareTo(prefix) < 0) high = middle - 1;
            else low = middle + 1;
        }
        return false;
    }

    internal void WritePrefixes(ref RespWriter writer)
    {
        foreach (var prefix in _wirePrefixes)
        {
            writer.WriteRaw("$6\r\nPREFIX\r\n"u8);
            prefix.WriteTo(ref writer);
        }
    }

    public int Count => _wirePrefixes.Length;
    public RespireKey this[int index] => _wirePrefixes[index];
    public IEnumerator<RespireKey> GetEnumerator() => ((IEnumerable<RespireKey>)_wirePrefixes).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
