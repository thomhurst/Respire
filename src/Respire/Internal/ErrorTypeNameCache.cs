using System.Runtime.CompilerServices;

namespace Respire.Internal;

/// <summary>Bounds canonical error-type labels without retaining collectible types.</summary>
internal sealed class ErrorTypeNameCache
{
    private readonly ConditionalWeakTable<Type, string> _types = new();
    // Different runtime types can share a FullName, including collectible replacements.
    // Charge that canonical name once; the first-seen budget lasts for this cache.
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly int _limit;
    private readonly ConditionalWeakTable<Type, string>.CreateValueCallback _createName;

    internal ErrorTypeNameCache(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
        _createName = CreateName;
    }

    internal string Get(Type type) => _types.GetValue(type, _createName);

    private string CreateName(Type type)
    {
        var name = type.FullName ?? type.Name;
        lock (_gate)
        {
            if (_names.Contains(name)) return name;
            if (_names.Count == _limit) return "_OTHER";
            _names.Add(name);
            return name;
        }
    }
}
