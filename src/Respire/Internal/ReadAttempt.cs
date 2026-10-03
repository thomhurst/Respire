namespace Respire.Internal;

// Created only after acquisition fails. Selection and role fallback share one attempt so
// exclusions, original errors, and retirement counts cannot diverge through value copies.
internal sealed class ReadAttempt
{
    private Dictionary<RespireEndpoint, Exception>? _failures;
    private Exception? _firstFailure;
    private int _retirements;

    internal bool IsFailed(RespireEndpoint endpoint) => _failures?.ContainsKey(endpoint) == true;

    internal static bool IsFailed(ReadAttempt? attempt, RespireEndpoint endpoint)
        => attempt is not null && attempt.IsFailed(endpoint);

    internal void Add(RespireEndpoint endpoint, Exception error, RespireEndpoint? alias = null)
    {
        var failures = _failures ??= new(RespireEndpointComparer.Instance);
        // Selection and rental may observe different endpoint identities. Every retry must
        // exclude a new identity; otherwise terminate with the first acquisition failure.
        if (!failures.TryAdd(endpoint, error))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[endpoint]).Throw();
        _firstFailure ??= error;
        if (alias is { } ownerEndpoint) failures.TryAdd(ownerEndpoint, error);
    }

    internal void ThrowFirstFailure()
    {
        if (_firstFailure is { } error)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    internal void ThrowIfFailed(RespireEndpoint endpoint)
    {
        if (_failures?.TryGetValue(endpoint, out var error) == true)
            throw new RespireConnectionException($"Read acquisition already failed at {endpoint}.", error);
    }

    // Retirement can publish a healthy replacement at the same address, so it does not exclude
    // an endpoint. Bound topology churn independently of the number of eligible read candidates.
    internal bool TryRetryRetirement() => _retirements++ < 5;
}
