using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadAttemptTests
{
    [Test]
    public async Task RepeatedEndpointRethrowsFirstFailureAndLookupRetainsCause()
    {
        var attempt = new ReadAttempt();
        var endpoint = new RespireEndpoint("replica", 6379);
        var original = new IOException("first acquisition failure");
        attempt.Add(endpoint, original);
        var lookup = await Assert.That(() => attempt.ThrowIfFailed(new("REPLICA", 6379)))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(lookup!.InnerException).IsSameReferenceAs(original);
        var repeated = await Assert.That(() => attempt.Add(endpoint, new IOException("later failure")))
            .ThrowsExactly<IOException>();
        await Assert.That(repeated).IsSameReferenceAs(original);
        await Assert.That(attempt.IsFailed(new("replica", 6380))).IsFalse();
    }

    [Test]
    public async Task RoleFallbackSharesRetirementBudgetAndEndpointFailures()
    {
        var attempt = new ReadAttempt();
        var fallback = attempt;
        await Assert.That(attempt.TryRetryRetirement()).IsTrue();
        await Assert.That(fallback.TryRetryRetirement()).IsTrue();
        await Assert.That(attempt.TryRetryRetirement()).IsTrue();
        await Assert.That(fallback.TryRetryRetirement()).IsTrue();
        await Assert.That(attempt.TryRetryRetirement()).IsTrue();
        await Assert.That(fallback.TryRetryRetirement()).IsFalse();
        var endpoint = new RespireEndpoint("replica", 6379);
        fallback.Add(endpoint, new IOException("failure"));
        await Assert.That(attempt.IsFailed(endpoint)).IsTrue();
        await Assert.That(attempt.TryRetryRetirement()).IsFalse();
    }
}
