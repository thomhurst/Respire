namespace Respire.Internal;

/// <summary>Completed replica cleanup failures retained until router disposal.</summary>
/// <remarks>The router lifecycle gate serializes additions and snapshots.</remarks>
internal sealed class ReadRetirementFailures
{
    internal const int MaximumRetained = 64;
    private readonly HashSet<Exception> _errors = new(ReferenceEqualityComparer.Instance);
    private long _omitted;

    internal void Add(Exception error)
    {
        if (_errors.Contains(error)) return;
        if (_errors.Count < MaximumRetained) _errors.Add(error);
        else _omitted++;
    }

    internal List<Exception> Snapshot()
    {
        var errors = _errors.ToList();
        if (_omitted > 0)
        {
            // Use a leaf exception so bulk cleanup's AggregateException.Flatten preserves
            // the count even when disposal combines this history with live cleanup failures.
            var summary = new InvalidOperationException(
                $"{_omitted} additional replica retirement cleanup failures occurred after retaining {MaximumRetained} distinct exceptions; their details were not retained.");
            summary.Data["OmittedRetirementFailureCount"] = _omitted;
            errors.Add(summary);
        }
        return errors;
    }
}
