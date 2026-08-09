using System.Text;

namespace Respire.Internal;

/// <summary>
/// Subscription-time string index with a UTF-8 span lookup for the message hot path.
/// </summary>
internal sealed class Utf8RouteDictionary<TValue>
{
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.Ordinal);

#if NET9_0_OR_GREATER
    private readonly Dictionary<Utf8RouteKey, Entry> _byUtf8 = new(Utf8RouteKeyComparer.Instance);
#endif

    public IEnumerable<string> Names => _byName.Keys;
    public IEnumerable<TValue> Values => _byName.Values.Select(static entry => entry.Value);

    public bool TryGetValue(string name, out TValue value)
    {
        if (_byName.TryGetValue(name, out var entry))
        {
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public bool TryGetValue(ReadOnlySpan<byte> utf8Name, out string name, out TValue value)
    {
#if NET9_0_OR_GREATER
        if (_byUtf8.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(utf8Name, out var entry))
#else
        var decoded = Encoding.UTF8.GetString(utf8Name);
        if (_byName.TryGetValue(decoded, out var entry))
#endif
        {
            name = entry.Name;
            value = entry.Value;
            return true;
        }

        name = null!;
        value = default!;
        return false;
    }

    public void Add(string name, TValue value)
    {
        var entry = new Entry(name, value);
        _byName.Add(name, entry);
#if NET9_0_OR_GREATER
        _byUtf8.Add(entry.Utf8Key, entry);
#endif
    }

    public bool Remove(string name)
    {
        if (!_byName.Remove(name, out var entry))
        {
            return false;
        }

#if NET9_0_OR_GREATER
        _byUtf8.Remove(entry.Utf8Key);
#endif
        return true;
    }

    public bool ContainsKey(string name) => _byName.ContainsKey(name);

    public void Clear()
    {
        _byName.Clear();
#if NET9_0_OR_GREATER
        _byUtf8.Clear();
#endif
    }

    private sealed class Entry
    {
        public Entry(string name, TValue value)
        {
            Name = name;
            Value = value;
#if NET9_0_OR_GREATER
            Utf8Key = new Utf8RouteKey(name);
#endif
        }

        public string Name { get; }
        public TValue Value { get; }

#if NET9_0_OR_GREATER
        public Utf8RouteKey Utf8Key { get; }
#endif
    }
}

#if NET9_0_OR_GREATER
internal sealed class Utf8RouteKey
{
    public Utf8RouteKey(string name)
    {
        Bytes = Encoding.UTF8.GetBytes(name);
        HashCode = Utf8RouteKeyComparer.Hash(Bytes);
    }

    public byte[] Bytes { get; }
    public int HashCode { get; }
}

internal sealed class Utf8RouteKeyComparer :
    IEqualityComparer<Utf8RouteKey>,
    IAlternateEqualityComparer<ReadOnlySpan<byte>, Utf8RouteKey>
{
    public static Utf8RouteKeyComparer Instance { get; } = new();

    public bool Equals(Utf8RouteKey? x, Utf8RouteKey? y)
        => ReferenceEquals(x, y) || (x is not null && y is not null && x.Bytes.AsSpan().SequenceEqual(y.Bytes));

    public int GetHashCode(Utf8RouteKey obj) => obj.HashCode;

    public bool Equals(ReadOnlySpan<byte> alternate, Utf8RouteKey other)
        => alternate.SequenceEqual(other.Bytes);

    public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);

    public Utf8RouteKey Create(ReadOnlySpan<byte> alternate)
        => new(Encoding.UTF8.GetString(alternate));

    internal static int Hash(ReadOnlySpan<byte> value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var item in value)
        {
            hash = (hash ^ item) * prime;
        }

        return unchecked((int)hash);
    }
}
#endif
