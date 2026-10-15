namespace Respire.Streaming;

/// <summary>How a flow handles terminal child failures, after all siblings finish.</summary>
public enum RespireJobFlowFailurePolicy
{
    /// <summary>Fail the parent without invoking its handler; propagate failure through ancestors.</summary>
    FailParent,
    /// <summary>Run the parent and omit failed children from its child results.</summary>
    Ignore,
    /// <summary>Run the parent with both successful and failed child results.</summary>
    Continue,
}

/// <summary>A node in a single-queue job tree. IDs must be unique within the flow.</summary>
public sealed record RespireJobFlow<T>(string Id, T Payload)
{
    /// <summary>Children that must finish before this job can run. Defaults to no children.</summary>
    public IReadOnlyList<RespireJobFlow<T>> Children { get; init; } = [];
}

/// <summary>A retained flow node snapshot. Result is owned binary data, or null if none was supplied.</summary>
/// <param name="Id">The application job ID within the flow.</param>
/// <param name="Status">One of waiting, ready, completed, or failed.</param>
/// <param name="Result">The successful handler's optional binary result.</param>
public sealed record RespireJobFlowResult(string Id, string Status,
    [param: System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Each snapshot owns its result bytes; access does not allocate or expose shared state.")]
    byte[]? Result);
