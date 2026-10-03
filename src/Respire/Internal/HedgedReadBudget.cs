namespace Respire.Internal;

/// <summary>One shared, bounded credit balance; an extra request costs 100 credits.</summary>
internal sealed class HedgedReadBudget(int percentage)
{
    private int _credits;

    internal void RecordRead()
    {
        var credits = Volatile.Read(ref _credits);
        while (credits < 100)
        {
            var observed = Interlocked.CompareExchange(ref _credits, Math.Min(100, credits + percentage), credits);
            if (observed == credits) return;
            credits = observed;
        }
    }

    internal bool HasCredit => Volatile.Read(ref _credits) == 100;

    internal bool TrySpend() => Interlocked.CompareExchange(ref _credits, 0, 100) == 100;
}
