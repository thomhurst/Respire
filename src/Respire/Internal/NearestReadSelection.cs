namespace Respire.Internal;

/// <summary>Allocation-free minimum selection; callers rotate enumeration order to break ties.</summary>
internal struct NearestReadSelection<T>
{
    private T _selected;
    private long _latency;
    private bool _linked;
    private bool _hasValue;

    internal void Consider(T candidate, long latency, bool linked = true)
    {
        if (_hasValue && (_linked && !linked || _linked == linked && latency >= _latency)) return;
        _selected = candidate;
        _latency = latency;
        _linked = linked;
        _hasValue = true;
    }

    internal bool TryGet(out T selected)
    {
        selected = _selected;
        return _hasValue;
    }
}
