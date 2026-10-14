namespace Respire.Internal;

// Candidate selection allocates nothing on success. Failure paths retain every cause
// without attributing a multi-endpoint result to whichever candidate happened to fail last.
internal static class ConnectionFailures
{
    internal static Exception AtEndpoint(Exception error, RespireEndpoint? endpoint)
        => endpoint is null || error is RespireException { Endpoint: not null } or OperationCanceledException
            or AggregateException or RespireException { InnerException: AggregateException }
            ? error : new RespireConnectionException(error.Message, error)
            {
                Endpoint = endpoint,
                IsCommandNotSubmitted = error is RespireException { IsCommandNotSubmitted: true },
            };

    internal static Exception? Combine(Exception? previous, Exception? current)
    {
        if (previous is null) return current;
        if (current is null || ReferenceEquals(previous, current)) return previous;
        return new AggregateException(previous, current);
    }
}
