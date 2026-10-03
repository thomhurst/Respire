namespace Respire.Internal;

// Created only after acquisition fails. Selection and role fallback share one attempt so
// exclusions, original errors, and retirement counts cannot diverge through value copies.
internal sealed class ReadAttempt
{
    private Dictionary<RespireEndpoint, Exception>? _failures;
    private Exception? _firstFailure;
    private int _retirements;

    internal Exception? FirstFailure => _firstFailure;

    internal bool ContainsFailure(RespireEndpoint endpoint) => _failures?.ContainsKey(endpoint) == true;

    internal bool TryAdd(RespireEndpoint endpoint, Exception error, RespireEndpoint? alias = null)
    {
        var failures = _failures ??= new(RespireEndpointComparer.Instance);
        // Every retry must exclude a new identity. The caller decides how to terminate
        // when this attempt has already recorded the failed endpoint.
        if (!failures.TryAdd(endpoint, error)) return false;
        _firstFailure ??= error;
        if (alias is { } ownerEndpoint) failures.TryAdd(ownerEndpoint, error);
        return true;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal void ThrowFirstFailure()
    {
        var error = _firstFailure ?? throw new InvalidOperationException("No read acquisition failure was recorded.");
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
