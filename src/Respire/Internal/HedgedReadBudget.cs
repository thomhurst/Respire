namespace Respire.Internal;

/// <summary>One shared, bounded credit balance; an extra request costs 100 credits.</summary>
internal sealed class HedgedReadBudget(int percentage)
{
    private readonly object _gate = new();
    private int _credits;

    internal void RecordRead()
    {
        lock (_gate) _credits = Math.Min(100, _credits + percentage);
    }

    internal bool HasCredit
    {
        get { lock (_gate) return _credits >= 100; }
    }

    internal bool TrySpend()
    {
        lock (_gate)
        {
            if (_credits < 100) return false;
            _credits -= 100;
            return true;
        }
    }
}
