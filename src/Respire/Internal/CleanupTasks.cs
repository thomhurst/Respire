namespace Respire.Internal;

/// <summary>Preserves every bulk cleanup failure across an async owner's await boundary.</summary>
internal static class CleanupTasks
{
    internal static async Task WhenAllAsync(IEnumerable<Task> tasks)
    {
        var completion = Task.WhenAll(tasks);
        try { await completion.ConfigureAwait(false); }
        catch when (completion.Exception is { InnerExceptions.Count: > 1 })
        {
            // Await alone exposes one exception. A single failure, including an aggregate
            // supplied by its owner, keeps its identity; multiple failures remain visible.
            throw completion.Exception.Flatten();
        }
    }
}
