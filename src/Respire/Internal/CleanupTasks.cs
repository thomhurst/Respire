namespace Respire.Internal;

/// <summary>Preserves every bulk cleanup failure across an async owner's await boundary.</summary>
internal static class CleanupTasks
{
    internal static async Task WhenAllAsync(IEnumerable<Task> tasks)
    {
        var completion = Task.WhenAll(tasks);
        try { await completion.ConfigureAwait(false); }
        catch when (completion.Exception is not null)
        {
            // Await alone exposes one exception. A single failure, including an aggregate
            // supplied by its owner, keeps its identity; multiple failures remain visible.
            Rethrow(completion.Exception.InnerExceptions);
        }
    }

    internal static void Rethrow(IReadOnlyList<Exception>? errors)
    {
        // Retirement and abort can observe the same failed cleanup task. Re-observing that
        // exception must not turn one failure into an aggregate or change its identity.
        if (errors is { Count: > 1 }) errors = errors.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
        if (errors is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors is { Count: > 1 })
        {
            var failures = new AggregateException(errors).Flatten().InnerExceptions
                .Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
            if (failures.Length == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException(failures);
        }
    }
}
