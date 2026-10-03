namespace Respire.Internal;

// The rental loop owns this mutable value. Selection receives read-only copies; a role fallback
// transfers it to the recursive rental and returns immediately. Successful reads allocate no set.
internal struct ReadAttempt
{
    private HashSet<RespireEndpoint>? _failedEndpoints;
    private int _retirements;

    internal readonly bool HasFailures => _failedEndpoints is not null;

    internal readonly bool IsFailed(RespireEndpoint endpoint) => _failedEndpoints?.Contains(endpoint) == true;

    internal void Add(RespireEndpoint endpoint)
        => (_failedEndpoints ??= new(RespireEndpointComparer.Instance)).Add(endpoint);

    internal readonly void ThrowIfFailed(RespireEndpoint endpoint)
    {
        if (IsFailed(endpoint))
            throw new RespireConnectionException($"Read acquisition already failed at {endpoint}.");
    }

    // Retirement can publish a healthy replacement at the same address, so it does not exclude
    // an endpoint. Bound topology churn independently of the number of eligible read candidates.
    internal bool TryRetryRetirement() => _retirements++ < 5;
}
