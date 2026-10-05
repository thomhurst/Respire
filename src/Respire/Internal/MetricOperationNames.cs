using System.Collections.Concurrent;

namespace Respire.Internal;

// Bound labels supplied through arbitrary raw commands. Traces retain their existing names.
internal sealed class MetricOperationNames(int maximumNames = 1024)
{
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    internal string GetName(string operation)
    {
        if (operation.Length is 0 or > 80) return "OTHER";
        foreach (var character in operation)
            if (!(char.IsAsciiLetterOrDigit(character) || character is ' ' or '.' or '_' or '-')) return "OTHER";
        if (_names.TryGetValue(operation, out var name)) return name;
        lock (_gate)
        {
            if (_names.TryGetValue(operation, out name)) return name;
            if (_names.Count == maximumNames) return "OTHER";
            name = operation.ToUpperInvariant();
            _names.TryAdd(name, name);
            return name;
        }
    }
}
