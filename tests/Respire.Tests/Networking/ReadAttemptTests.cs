using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadAttemptTests
{
    [Test]
    public async Task RepeatedEndpointDeclinesRetryAndRetainsFirstFailure()
    {
        var attempt = new ReadAttempt();
        var endpoint = new RespireEndpoint("replica", 6379);
        var original = new IOException("first acquisition failure");
        await Assert.That(attempt.TryAdd(endpoint, original)).IsTrue();
        var lookup = await Assert.That(() => attempt.ThrowIfFailed(new("REPLICA", 6379)))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(lookup!.InnerException).IsSameReferenceAs(original);
        await Assert.That(attempt.TryAdd(endpoint, new IOException("later failure"))).IsFalse();
        var repeated = await Assert.That(() => attempt.ThrowFirstFailure())
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
        await Assert.That(fallback.TryAdd(endpoint, new IOException("failure"))).IsTrue();
        await Assert.That(attempt.IsFailed(endpoint)).IsTrue();
        await Assert.That(attempt.TryRetryRetirement()).IsFalse();
    }
}
