namespace Respire.Internal;

// Candidate selection allocates nothing on success. Failure paths retain every cause
// without attributing a multi-endpoint result to whichever candidate happened to fail last.
internal static class ConnectionFailures
{
    // Diagnostic context only: callers must retain the original exception for retry
    // decisions, lifecycle events, and other type-based classification.
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
        if (previous is not AggregateException && current is not AggregateException)
            return new AggregateException(previous, current);

        var causes = new List<Exception>();
        Append(previous, causes);
        Append(current, causes);
        return new AggregateException(causes);

        static void Append(Exception error, List<Exception> causes)
        {
            if (error is AggregateException aggregate)
            {
                foreach (var cause in aggregate.InnerExceptions) Append(cause, causes);
            }
            else causes.Add(error);
        }
    }
}
