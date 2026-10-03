namespace Respire.Internal;

internal static class ReadAttemptExtensions
{
    internal static bool IsFailed(this ReadAttempt? attempt, RespireEndpoint endpoint)
        => attempt is not null && attempt.ContainsFailure(endpoint);
}
